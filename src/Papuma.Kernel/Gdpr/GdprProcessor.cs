// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Events;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Provides GDPR-oriented history and redaction operations for stored events.
/// Callers should enforce authorization and rate-limiting before invoking redaction methods.
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
    /// Redacts all change feed and business event log entries for a specific entity and scope.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="actorId">The actor performing the redaction.</param>
    /// <param name="reason">The documented reason for the redaction.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<RedactionResult> RedactEntityAsync(
        ScopeContext scope,
        string entity,
        string entityId,
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        InputValidator.ValidateEntity(entity);
        InputValidator.ValidateEntityId(entityId);
        InputValidator.ValidateActorId(actorId);

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
                        WHERE scope     = @scope
                            AND (
                                     (@tenantId IS NULL AND tenant_id IS NULL)
                                     OR
                                     tenant_id = @tenantId
                            )
              AND entity    = @entity
              AND entity_id = @entityId
              AND redacted  = FALSE
            """;
                feedCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
                feedCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        feedCmd.Parameters.AddWithValue("entity", entity);
        feedCmd.Parameters.AddWithValue("entityId", entityId);
        var feedAffected = await feedCmd.ExecuteNonQueryAsync(ct);

        await using var belCmd = conn.CreateCommand();
        belCmd.Transaction = tx;
        belCmd.CommandText = """
            UPDATE business_event_log
            SET payload  = '{"redacted": true}'::jsonb,
                redacted = TRUE
                        WHERE scope     = @scope
                            AND (
                                     (@tenantId IS NULL AND tenant_id IS NULL)
                                     OR
                                     tenant_id = @tenantId
                            )
              AND entity    = @entity
              AND entity_id = @entityId
              AND redacted  = FALSE
            """;
                belCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
                belCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
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
            scope,
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
    /// Returns the stored history for one entity and scope across change feed and business event log.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<EntityHistory> GetEntityHistoryAsync(
        ScopeContext scope,
        string entity,
        string entityId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        InputValidator.ValidateEntity(entity);
        InputValidator.ValidateEntityId(entityId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var feedCmd = conn.CreateCommand();
        feedCmd.CommandText = """
             SELECT sequence_id, entity, entity_id, event_type, version,
                 correlation_id, causation_id, actor_id, payload::text, timestamp, scope, tenant_id
            FROM change_feed
             WHERE scope     = @scope
            AND (
                 (@tenantId IS NULL AND tenant_id IS NULL)
                 OR
                 tenant_id = @tenantId
            )
              AND entity    = @entity
              AND entity_id = @entityId
            ORDER BY sequence_id
            """;
         feedCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
         feedCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
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
                    Scope: Enum.Parse<ScopeType>(feedReader.GetString(10), ignoreCase: false),
                    TenantId: feedReader.IsDBNull(11) ? null : feedReader.GetString(11)));
            }
        }

        await using var belCmd = conn.CreateCommand();
        belCmd.CommandText = """
                        SELECT event_id, event_type, actor_id, payload::text, occurred_at, scope, tenant_id
            FROM business_event_log
                        WHERE scope     = @scope
                            AND (
                                     (@tenantId IS NULL AND tenant_id IS NULL)
                                     OR
                                     tenant_id = @tenantId
                            )
              AND entity    = @entity
              AND entity_id = @entityId
            ORDER BY occurred_at
            """;
                belCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
                belCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
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
                    OccurredAt: belReader.GetFieldValue<DateTimeOffset>(4),
                    Scope: Enum.Parse<ScopeType>(belReader.GetString(5), ignoreCase: false),
                    TenantId: belReader.IsDBNull(6) ? null : belReader.GetString(6)));
            }
        }

        return new EntityHistory(changeRecords, businessEvents);
    }
}
