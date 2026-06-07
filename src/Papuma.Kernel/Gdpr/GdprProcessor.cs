// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Provides GDPR-oriented history and redaction operations for the unified event feed.
/// Callers should enforce authorization and rate-limiting before invoking redaction methods.
/// </summary>
public sealed class GdprProcessor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ChangeWriter _changeWriter;
    private readonly ILogger<GdprProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GdprProcessor"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="changeWriter">The unified event feed writer for audit trail events.</param>
    /// <param name="logger">The logger.</param>
    public GdprProcessor(
        NpgsqlDataSource dataSource,
        ChangeWriter changeWriter,
        ILogger<GdprProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(changeWriter);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _changeWriter = changeWriter;
        _logger = logger;
    }

    /// <summary>
    /// Redacts all event feed records for a given entity within the scope, marks them as redacted,
    /// and writes an audit trail <c>EntityRedacted</c> event.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="actorId">The actor performing the redaction.</param>
    /// <param name="reason">The reason for redaction.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A result containing the number of affected records.</returns>
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

        await conn.SetScopeAsync(scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE event_feed
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
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("entity", entity);
        cmd.Parameters.AddWithValue("entityId", entityId);
        var affected = await cmd.ExecuteNonQueryAsync(ct);

        var auditPayload = JsonSerializer.Serialize(new
        {
            RedactedEntity = entity,
            RedactedEntityId = entityId,
            Reason = reason,
            EventsRedacted = affected,
            RedactedAt = DateTimeOffset.UtcNow,
        });

        await _changeWriter.AppendEventAsync(
            tx,
            scope,
            eventType: "EntityRedacted",
            actorId,
            payloadJson: auditPayload,
            entity: entity,
            entityId: entityId,
            ct: ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "GDPR redaction completed: entity={Entity}, entityId={EntityId}, actor={ActorId}, events={Affected}, reason={Reason}",
            entity,
            entityId,
            actorId,
            affected,
            reason);

        return new RedactionResult(affected);
    }

    /// <summary>
    /// Returns the complete event feed history for a given entity within the scope, including redacted records.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The entity history containing all matching records.</returns>
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

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
             SELECT sequence_id, kind, event_id, scope, tenant_id, entity, entity_id, event_type, version,
                 correlation_id, causation_id, actor_id, payload::text, occurred_at
            FROM event_feed
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
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("entity", entity);
        cmd.Parameters.AddWithValue("entityId", entityId);

        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                SequenceId: reader.GetInt64(0),
                Kind: reader.GetString(1),
                EventId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                Entity: reader.IsDBNull(4) ? null : reader.GetString(4),
                EntityId: reader.IsDBNull(5) ? null : reader.GetString(5),
                EventType: reader.GetString(6),
                Version: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                CorrelationId: reader.IsDBNull(8) ? null : reader.GetString(8),
                CausationId: reader.IsDBNull(9) ? null : reader.GetString(9),
                ActorId: reader.GetString(10),
                PayloadJson: reader.GetString(11),
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(12),
                Scope: Enum.Parse<ScopeType>(reader.GetString(3), ignoreCase: false),
                TenantId: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new EntityHistory(records);
    }
}
