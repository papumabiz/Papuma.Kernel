// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.ChangeFeed;

/// <summary>
/// Reads event feed records for ad-hoc queries (debugging, exports, admin tools).
/// </summary>
public sealed class ChangeFeedReader
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeFeedReader"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    public ChangeFeedReader(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Returns all event feed records for a specific entity, ordered by sequence.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The matching event feed records.</returns>
    public async Task<IReadOnlyList<ChangeRecord>> GetByEntityAsync(
        ScopeContext scope,
        string entity,
        string entityId,
        int limit = 1000,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        InputValidator.ValidateEntity(entity);
        InputValidator.ValidateEntityId(entityId);
        ValidateLimit(limit);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT sequence_id, kind, event_id, scope, tenant_id, entity, entity_id, event_type, version,
                   correlation_id, causation_id, actor_id, payload::text, occurred_at
            FROM papuma_event_feed
            WHERE entity = @entity
              AND entity_id = @entityId
              AND (@scope IS NULL OR scope = @scope)
              AND (
                  @scope IS NULL
                  OR
                  @scope <> 'Tenant'
                  OR
                  tenant_id = @tenantId
              )
            ORDER BY sequence_id
            LIMIT @limit
            """;

        cmd.Parameters.AddWithValue("entity", entity);
        cmd.Parameters.AddWithValue("entityId", entityId);
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit);

        return await ReadRecordsAsync(cmd, ct);
    }

    /// <summary>
    /// Returns event feed records within the given sequence range, ordered by sequence.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="fromSequenceId">The inclusive lower sequence bound.</param>
    /// <param name="toSequenceId">The inclusive upper sequence bound.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The matching event feed records.</returns>
    public async Task<IReadOnlyList<ChangeRecord>> GetBySequenceRangeAsync(
        ScopeContext scope,
        long fromSequenceId,
        long toSequenceId,
        int limit = 1000,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateLimit(limit);

        if (fromSequenceId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fromSequenceId), "fromSequenceId must be greater than or equal to 1.");
        }

        if (toSequenceId < fromSequenceId)
        {
            throw new ArgumentOutOfRangeException(nameof(toSequenceId), "toSequenceId must be greater than or equal to fromSequenceId.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT sequence_id, kind, event_id, scope, tenant_id, entity, entity_id, event_type, version,
                   correlation_id, causation_id, actor_id, payload::text, occurred_at
            FROM papuma_event_feed
            WHERE sequence_id BETWEEN @fromSequenceId AND @toSequenceId
              AND (@scope IS NULL OR scope = @scope)
              AND (
                  @scope IS NULL
                  OR
                  @scope <> 'Tenant'
                  OR
                  tenant_id = @tenantId
              )
            ORDER BY sequence_id
            LIMIT @limit
            """;

        cmd.Parameters.AddWithValue("fromSequenceId", fromSequenceId);
        cmd.Parameters.AddWithValue("toSequenceId", toSequenceId);
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit);

        return await ReadRecordsAsync(cmd, ct);
    }

    public async Task<long> GetLatestSequenceIdAsync(
        ScopeContext scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COALESCE(MAX(sequence_id), 0)
            FROM papuma_event_feed
            WHERE (@scope IS NULL OR scope = @scope)
              AND (
                  @scope IS NULL
                  OR
                  @scope <> 'Tenant'
                  OR
                  tenant_id = @tenantId
              )
            """;

        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long latestSequenceId ? latestSequenceId : 0L;
    }

    /// <summary>
    /// Reads all matching records from the command into a list of <see cref="ChangeRecord"/>.
    /// </summary>
    private static async Task<IReadOnlyList<ChangeRecord>> ReadRecordsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
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

        return records;
    }

    private static void ValidateLimit(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
        }
    }
}
