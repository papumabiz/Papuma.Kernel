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
    /// Returns a page of event feed records for a specific entity, ordered by sequence.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    /// <param name="entity">The logical entity name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="afterSequenceId">
    /// Cursor for keyset pagination. Pass <c>0</c> (default) to start from the beginning,
    /// or the <see cref="ChangeRecordPage.NextCursorSequenceId"/> from the previous page to continue.
    /// </param>
    /// <param name="limit">The maximum number of records per page.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// A page of matching records. Check <see cref="ChangeRecordPage.HasMore"/> to determine
    /// whether additional pages are available.
    /// </returns>
    public async Task<ChangeRecordPage> GetByEntityAsync(
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
        ValidateLimit(limit);

        if (afterSequenceId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequenceId), "afterSequenceId must be greater than or equal to 0.");
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
            WHERE entity = @entity
              AND entity_id = @entityId
              AND sequence_id > @afterSequenceId
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
        cmd.Parameters.AddWithValue("afterSequenceId", afterSequenceId);
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        // Request one extra record to detect whether more pages exist.
        cmd.Parameters.AddWithValue("limit", limit + 1);

        var all = await ReadRecordsAsync(cmd, ct);
        await tx.CommitAsync(ct);

        var hasMore = all.Count > limit;
        var records = hasMore ? all.Take(limit).ToList() : (IReadOnlyList<ChangeRecord>)all;
        var nextCursor = hasMore ? records[^1].SequenceId : (long?)null;

        return new ChangeRecordPage(records, hasMore, nextCursor);
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

        var result = await ReadRecordsAsync(cmd, ct);
        await tx.CommitAsync(ct);
        return result;
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

        var scalar = await cmd.ExecuteScalarAsync(ct);
        var latestSequenceId = scalar is long id ? id : 0L;
        await tx.CommitAsync(ct);
        return latestSequenceId;
    }

    /// <summary>
    /// Reads all matching records from the command into a list of <see cref="ChangeRecord"/>.
    /// </summary>
    private static async Task<IReadOnlyList<ChangeRecord>> ReadRecordsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ChangeRecordMapper.ReadAllAsync(reader, ct);
    }

    private static void ValidateLimit(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
        }
    }
}
