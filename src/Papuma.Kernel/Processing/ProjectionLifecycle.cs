// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Processing;

/// <summary>
/// Registration, versioned rebuild and reset of feed handlers (ADR-024), shared by the
/// change and the event processor. <paramref name="key"/> parameters are checkpoint keys
/// (the event feed prefixes <c>event:</c>); failure entries use the same key.
/// </summary>
internal static class ProjectionLifecycle
{
    /// <summary>
    /// Registers a handler's checkpoint on first sight — at the beginning, or at the head
    /// for <see cref="StartsAtFeedHeadAttribute"/> — and reconciles a projection's stored
    /// version with the declared one: record it, rebuild, or leave it to a newer instance.
    /// </summary>
    public static async Task RegisterAsync(
        NpgsqlDataSource dataSource, string key, string table, object handler, int? version,
        ILogger logger, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(tx, ct); // max(seq) below reads the feed table under RLS

        var atHead = HandlerKind.StartsAtHead(handler);
        await using (var insert = conn.CreateCommand())
        {
            // At the head: everything visible now counts as delivered (ADR-022 cursor).
            insert.Transaction = tx;
            insert.CommandText = $"""
                INSERT INTO papuma.checkpoint (handler_name, last_seq, base_seq, done_snapshot, projection_version)
                SELECT @name,
                       CASE WHEN @atHead THEN (SELECT COALESCE(max(seq), 0) FROM {table}) ELSE 0 END,
                       0,
                       CASE WHEN @atHead THEN pg_current_snapshot() END,
                       @version
                ON CONFLICT (handler_name) DO NOTHING
                RETURNING true
                """;
            insert.Parameters.AddWithValue("name", key);
            insert.Parameters.AddWithValue("atHead", atHead);
            insert.Parameters.Add(new NpgsqlParameter("version", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)version ?? DBNull.Value });
            if (await insert.ExecuteScalarAsync(ct) is true)
            {
                await tx.CommitAsync(ct);
                if (atHead)
                {
                    logger.LogInformation("Handler {Handler} registered at the feed head — earlier records are not delivered to it.", key);
                }

                return;
            }
        }

        if (version is not int declared)
        {
            await tx.CommitAsync(ct);
            return;
        }

        var stored = await LockVersionAsync(conn, tx, key, ct);
        if (stored is null)
        {
            // First start with ADR-024: record, don't rebuild what already exists.
            await SetVersionAsync(conn, tx, key, declared, ct);
        }
        else if (stored > declared)
        {
            logger.LogWarning(
                "Projection {Handler} is at version {Stored}, this instance declares {Declared} — it runs older code and pauses the projection.",
                key, stored, declared);
        }
        else if (stored < declared)
        {
            logger.LogInformation(
                "Projection {Handler} changed from version {Stored} to {Declared} — resetting its target and replaying the feed.",
                key, stored, declared);
            await ((IProjection)handler).ResetAsync(ct);
            await ResetCursorAsync(conn, tx, key, declared, ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Resets a projection — its target via <see cref="IProjection.ResetAsync"/>, then its
    /// cursor and failures — unless a newer instance owns it (stored version higher).
    /// </summary>
    /// <returns><c>false</c> when the stored version is higher and nothing was reset.</returns>
    public static async Task<bool> ResetProjectionAsync(
        NpgsqlDataSource dataSource, string key, IProjection projection, int declared,
        ILogger logger, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var stored = await LockVersionAsync(conn, tx, key, ct);
        if (stored > declared)
        {
            logger.LogWarning(
                "Projection {Handler} is at version {Stored}, this instance declares {Declared} — not resetting it.",
                key, stored, declared);
            await tx.CommitAsync(ct);
            return false;
        }

        await projection.ResetAsync(ct);
        await ResetCursorAsync(conn, tx, key, declared, ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Projection {Handler} reset — full replay on the next cycle.", key);
        return true;
    }

    /// <summary>Resets a checkpoint to "nothing delivered" and clears its failures.</summary>
    public static async Task ResetCursorAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, int? version, CancellationToken ct)
    {
        await using (var reset = conn.CreateCommand())
        {
            reset.Transaction = tx;
            reset.CommandText = SnapshotCursor.ResetSql;
            reset.Parameters.AddWithValue("name", key);
            await reset.ExecuteNonQueryAsync(ct);
        }

        await using (var failures = conn.CreateCommand())
        {
            failures.Transaction = tx;
            failures.CommandText = "DELETE FROM papuma.failure WHERE handler_name = @name";
            failures.Parameters.AddWithValue("name", key);
            await failures.ExecuteNonQueryAsync(ct);
        }

        if (version is int declared)
        {
            await SetVersionAsync(conn, tx, key, declared, ct);
        }
    }

    // Waits for another instance's cycle on this handler to finish (FOR UPDATE, not SKIP
    // LOCKED): a rebuild must not interleave with a delivery.
    private static async Task<int?> LockVersionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT projection_version FROM papuma.checkpoint WHERE handler_name = @name FOR UPDATE";
        cmd.Parameters.AddWithValue("name", key);
        return await cmd.ExecuteScalarAsync(ct) is int stored ? stored : null;
    }

    private static async Task SetVersionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, int version, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE papuma.checkpoint SET projection_version = @version WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", key);
        cmd.Parameters.AddWithValue("version", version);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
