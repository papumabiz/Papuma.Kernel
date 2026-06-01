// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Events;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Provides GDPR-oriented history and redaction operations for stored events.
/// </summary>
public sealed class GdprProcessor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly BusinessEventWriter _businessEventWriter;
    private readonly ILogger<GdprProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GdprProcessor"/> class.
    /// </summary>
    /// <param name="dataSource">The data source used for redaction and history queries.</param>
    /// <param name="businessEventWriter">The writer used to audit redaction operations.</param>
    /// <param name="logger">The logger used for operational diagnostics.</param>
    public GdprProcessor(
        NpgsqlDataSource dataSource,
        BusinessEventWriter businessEventWriter,
        ILogger<GdprProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(businessEventWriter);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _businessEventWriter = businessEventWriter;
        _logger = logger;
    }

    /// <summary>
    /// Redacts all change feed and business event log entries for a specific entity in one transaction.
    /// </summary>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="actorId">The actor performing the redaction.</param>
    /// <param name="reason">The documented reason for the redaction.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<RedactionResult> RedactEntityAsync(
        string entity,
        string entityId,
        string actorId,
        string reason,
        CancellationToken ct = default)
        => await RedactEntityAsync(
            TenantContext.Default,
            entity,
            entityId,
            actorId,
            reason,
            ct);

    /// <summary>
    /// Redacts all change feed and business event log entries for a specific entity in one tenant.
    /// </summary>
    /// <param name="tenant">The tenant context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="actorId">The actor performing the redaction.</param>
    /// <param name="reason">The documented reason for the redaction.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<RedactionResult> RedactEntityAsync(
        TenantContext tenant,
        string entity,
        string entityId,
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ValidateEntityReference(entity, entityId);

        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("actorId is required for GDPR redaction.", nameof(actorId));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason is required for GDPR redaction.", nameof(reason));
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var feedCmd = conn.CreateCommand();
        feedCmd.Transaction = tx;
        feedCmd.CommandText = """
            UPDATE change_feed
            SET payload  = '{"redacted": true}'::jsonb,
                redacted = TRUE
                        WHERE tenant_id = @tenantId
                            AND entity    = @entity
              AND entity_id = @entityId
              AND redacted  = FALSE
            """;
                feedCmd.Parameters.AddWithValue("tenantId", tenant.TenantId);
        feedCmd.Parameters.AddWithValue("entity", entity);
        feedCmd.Parameters.AddWithValue("entityId", entityId);
        var feedAffected = await feedCmd.ExecuteNonQueryAsync(ct);

        await using var belCmd = conn.CreateCommand();
        belCmd.Transaction = tx;
        belCmd.CommandText = """
            UPDATE business_event_log
            SET payload  = '{"redacted": true}'::jsonb,
                redacted = TRUE
                        WHERE tenant_id = @tenantId
                            AND entity    = @entity
              AND entity_id = @entityId
              AND redacted  = FALSE
            """;
                belCmd.Parameters.AddWithValue("tenantId", tenant.TenantId);
        belCmd.Parameters.AddWithValue("entity", entity);
        belCmd.Parameters.AddWithValue("entityId", entityId);
        var businessEventsAffected = await belCmd.ExecuteNonQueryAsync(ct);

        var auditPayload = JsonSerializer.Serialize(new
        {
            RedactedEntity = entity,
            RedactedEntityId = entityId,
            Reason = reason,
            FeedEventsRedacted = feedAffected,
            BusinessEventsRedacted = businessEventsAffected,
            RedactedAt = DateTimeOffset.UtcNow,
        });

        await _businessEventWriter.AppendAsync(
            tx,
            tenant,
            eventType: "EntityRedacted",
            actorId,
            payloadJson: auditPayload,
            ct: ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "GDPR redaction completed: entity={Entity}, entityId={EntityId}, actor={ActorId}, feedEvents={FeedAffected}, businessEvents={BusinessEventsAffected}, reason={Reason}",
            entity,
            entityId,
            actorId,
            feedAffected,
            businessEventsAffected,
            reason);

        return new RedactionResult(feedAffected, businessEventsAffected);
    }

    /// <summary>
    /// Returns the stored history for one entity across change feed and business event log.
    /// </summary>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<EntityHistory> GetEntityHistoryAsync(
        string entity,
        string entityId,
        CancellationToken ct = default)
        => await GetEntityHistoryAsync(
            TenantContext.Default,
            entity,
            entityId,
            ct);

    /// <summary>
    /// Returns the stored history for one entity and tenant across change feed and business event log.
    /// </summary>
    /// <param name="tenant">The tenant context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<EntityHistory> GetEntityHistoryAsync(
        TenantContext tenant,
        string entity,
        string entityId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ValidateEntityReference(entity, entityId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var feedCmd = conn.CreateCommand();
        feedCmd.CommandText = """
            SELECT sequence_id, entity, entity_id, event_type, version,
                                     correlation_id, causation_id, actor_id, payload::text, timestamp, tenant_id
            FROM change_feed
                        WHERE tenant_id = @tenantId
                            AND entity    = @entity
              AND entity_id = @entityId
            ORDER BY sequence_id
            """;
                feedCmd.Parameters.AddWithValue("tenantId", tenant.TenantId);
        feedCmd.Parameters.AddWithValue("entity", entity);
        feedCmd.Parameters.AddWithValue("entityId", entityId);

        var changeRecords = new List<ChangeRecord>();
        await using (var feedReader = await feedCmd.ExecuteReaderAsync(ct))
        {
            while (await feedReader.ReadAsync(ct))
            {
                changeRecords.Add(new ChangeRecord(
                    SequenceId: feedReader.GetInt64(0),
                    Entity: feedReader.GetString(1),
                    EntityId: feedReader.GetString(2),
                    EventType: feedReader.GetString(3),
                    Version: feedReader.GetInt32(4),
                    CorrelationId: feedReader.IsDBNull(5) ? null : feedReader.GetString(5),
                    CausationId: feedReader.IsDBNull(6) ? null : feedReader.GetString(6),
                    ActorId: feedReader.GetString(7),
                    PayloadJson: feedReader.GetString(8),
                    Timestamp: feedReader.GetFieldValue<DateTimeOffset>(9),
                    TenantId: feedReader.GetString(10)));
            }
        }

        await using var belCmd = conn.CreateCommand();
        belCmd.CommandText = """
            SELECT event_id, event_type, actor_id, payload::text, occurred_at
            FROM business_event_log
                        WHERE tenant_id = @tenantId
                            AND entity    = @entity
              AND entity_id = @entityId
            ORDER BY occurred_at
            """;
                belCmd.Parameters.AddWithValue("tenantId", tenant.TenantId);
        belCmd.Parameters.AddWithValue("entity", entity);
        belCmd.Parameters.AddWithValue("entityId", entityId);

        var businessEvents = new List<BusinessEventRecord>();
        await using (var belReader = await belCmd.ExecuteReaderAsync(ct))
        {
            while (await belReader.ReadAsync(ct))
            {
                businessEvents.Add(new BusinessEventRecord(
                    EventId: belReader.GetGuid(0),
                    EventType: belReader.GetString(1),
                    ActorId: belReader.GetString(2),
                    PayloadJson: belReader.GetString(3),
                    OccurredAt: belReader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        return new EntityHistory(changeRecords, businessEvents);
    }

    private static void ValidateEntityReference(string entity, string entityId)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            throw new ArgumentException("entity is required.", nameof(entity));
        }

        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("entityId is required.", nameof(entityId));
        }
    }
}