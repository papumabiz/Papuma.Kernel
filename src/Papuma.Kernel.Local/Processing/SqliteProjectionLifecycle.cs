// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.Processing;

/// <summary>
/// Registration, versioned rebuild and reset of feed handlers (ADR-024) for the SQLite
/// kernel, shared by its change and event processor. Unlike PostgreSQL, a projection's
/// <see cref="IProjection.ResetAsync"/> runs with no transaction open: it may write to
/// the same database file, and the file has one write lock. The checkpoint is reset
/// afterwards; a crash in between leaves the old version stored, so the next start
/// repeats both.
/// </summary>
internal static class SqliteProjectionLifecycle
{
    /// <summary>
    /// Registers a handler's checkpoint on first sight — at the beginning, or at the head
    /// for <see cref="StartsAtFeedHeadAttribute"/> — and reconciles a projection's stored
    /// version with the declared one.
    /// </summary>
    public static async Task RegisterAsync(
        string connectionString, string key, string table, object handler, int? version,
        ILogger logger, CancellationToken ct)
    {
        var atHead = HandlerKind.StartsAtHead(handler);
        int? stored;
        await using (var conn = await SqliteConnectionFactory.OpenAsync(connectionString, ct))
        await using (var tx = conn.BeginTransaction(deferred: false))
        {
            await using (var insert = conn.CreateCommand())
            {
                // One writer: seq order is commit order, so "at the head" is max(seq).
                insert.Transaction = tx;
                insert.CommandText = $"""
                    INSERT INTO checkpoint (handler_name, last_seq, projection_version, updated_at)
                    VALUES (@name,
                            CASE WHEN @atHead THEN (SELECT COALESCE(max(seq), 0) FROM {table}) ELSE 0 END,
                            @version, @now)
                    ON CONFLICT (handler_name) DO NOTHING
                    RETURNING 1
                    """;
                insert.Parameters.AddWithValue("name", key);
                insert.Parameters.AddWithValue("atHead", atHead);
                insert.Parameters.AddWithValue("version", (object?)version ?? DBNull.Value);
                insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
                if (await insert.ExecuteScalarAsync(ct) is not null)
                {
                    await tx.CommitAsync(ct);
                    if (atHead)
                    {
                        logger.LogInformation("Handler {Handler} registered at the feed head — earlier records are not delivered to it.", key);
                    }

                    return;
                }
            }

            if (version is not int declaredVersion)
            {
                await tx.CommitAsync(ct);
                return;
            }

            stored = await ReadVersionAsync(conn, tx, key, ct);
            if (stored is null)
            {
                // First start with ADR-024: record, don't rebuild what already exists.
                await SetVersionAsync(conn, tx, key, declaredVersion, ct);
            }

            await tx.CommitAsync(ct);
        }

        var declared = version.Value;
        if (stored > declared)
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
            await ResetProjectionAsync(connectionString, key, (IProjection)handler, declared, logger, ct);
        }
    }

    /// <summary>
    /// Resets a projection — its target first, then its checkpoint and failures — unless
    /// the stored version is higher.
    /// </summary>
    /// <returns><c>false</c> when the stored version is higher and nothing was reset.</returns>
    public static async Task<bool> ResetProjectionAsync(
        string connectionString, string key, IProjection projection, int declared,
        ILogger logger, CancellationToken ct)
    {
        await using (var conn = await SqliteConnectionFactory.OpenAsync(connectionString, ct))
        {
            if (await ReadVersionAsync(conn, tx: null, key, ct) > declared)
            {
                logger.LogWarning("Projection {Handler} is at a higher version than this instance declares — not resetting it.", key);
                return false;
            }
        }

        await projection.ResetAsync(ct); // no transaction open: it may write to this file

        await using (var conn = await SqliteConnectionFactory.OpenAsync(connectionString, ct))
        await using (var tx = conn.BeginTransaction(deferred: false))
        {
            await ResetCursorAsync(conn, tx, key, declared, ct);
            await tx.CommitAsync(ct);
        }

        logger.LogInformation("Projection {Handler} reset — full replay on the next cycle.", key);
        return true;
    }

    /// <summary>Resets a checkpoint to 0 and clears its failures; records the version when given.</summary>
    public static async Task ResetCursorAsync(
        SqliteConnection conn, SqliteTransaction tx, string key, int? version, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var reset = conn.CreateCommand())
        {
            reset.Transaction = tx;
            reset.CommandText = """
                INSERT INTO checkpoint (handler_name, last_seq, updated_at)
                VALUES (@name, 0, @now)
                ON CONFLICT (handler_name) DO UPDATE SET last_seq = 0, updated_at = @now
                """;
            reset.Parameters.AddWithValue("name", key);
            reset.Parameters.AddWithValue("now", now);
            await reset.ExecuteNonQueryAsync(ct);
        }

        await using (var failures = conn.CreateCommand())
        {
            failures.Transaction = tx;
            failures.CommandText = "DELETE FROM failure WHERE handler_name = @name";
            failures.Parameters.AddWithValue("name", key);
            await failures.ExecuteNonQueryAsync(ct);
        }

        if (version is int declared)
        {
            await SetVersionAsync(conn, tx, key, declared, ct);
        }
    }

    /// <summary>Reads the stored projection version, or <c>null</c>.</summary>
    public static async Task<int?> ReadVersionAsync(
        SqliteConnection conn, SqliteTransaction? tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT projection_version FROM checkpoint WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", key);
        return await cmd.ExecuteScalarAsync(ct) is long stored ? (int)stored : null;
    }

    private static async Task SetVersionAsync(
        SqliteConnection conn, SqliteTransaction tx, string key, int version, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE checkpoint SET projection_version = @version WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", key);
        cmd.Parameters.AddWithValue("version", version);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
