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
            UPDATE papuma_event_feed
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
    /// Returns a page of event feed history for a given entity within the scope, including redacted records.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="afterSequenceId">
    /// Cursor for keyset pagination. Pass <c>0</c> (default) to start from the beginning,
    /// or the <see cref="EntityHistory.NextCursorSequenceId"/> from the previous page to continue.
    /// </param>
    /// <param name="limit">The maximum number of records per page (default: 1000).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// A page of entity history. Check <see cref="EntityHistory.HasMore"/> to determine
    /// whether additional pages are available.
    /// </returns>
    public async Task<EntityHistory> GetEntityHistoryAsync(
        ScopeContext scope,
        string entity,
        string entityId,
        long afterSequenceId = 0,
        int limit = 1000,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        InputValidator.ValidateEntity(entity);
        InputValidator.ValidateEntityId(entityId);

        if (afterSequenceId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequenceId), "afterSequenceId must be greater than or equal to 0.");
        }

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Fetch limit + 1 to detect whether a next page exists without a separate COUNT query.
        cmd.CommandText = """
            SELECT sequence_id, kind, event_id, scope, tenant_id, entity, entity_id, event_type, version,
                   correlation_id, causation_id, actor_id, payload::text, occurred_at
            FROM papuma_event_feed
            WHERE scope = @scope
              AND (
                  (@tenantId IS NULL AND tenant_id IS NULL)
                  OR
                  tenant_id = @tenantId
              )
              AND entity      = @entity
              AND entity_id   = @entityId
              AND sequence_id > @afterSequenceId
            ORDER BY sequence_id
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("entity", entity);
        cmd.Parameters.AddWithValue("entityId", entityId);
        cmd.Parameters.AddWithValue("afterSequenceId", afterSequenceId);
        // Request one extra record to detect whether more pages exist.
        cmd.Parameters.AddWithValue("limit", limit + 1);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var all = await ChangeRecordMapper.ReadAllAsync(reader, ct);
        await reader.CloseAsync();
        await tx.CommitAsync(ct);

        var hasMore = all.Count > limit;
        var records = hasMore ? all.Take(limit).ToList() : (IReadOnlyList<ChangeRecord>)all;
        var nextCursor = hasMore ? records[^1].SequenceId : (long?)null;

        return new EntityHistory(records, hasMore, nextCursor);
    }
}
