// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

namespace Papuma.Kernel.Store;

/// <summary>
/// Opens SQLite connections with the kernel's required PRAGMAs applied — the single
/// place every connection in <c>Papuma.Kernel.Local</c> goes through, so a future call
/// site can't accidentally open one without them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>journal_mode = WAL</c>: the default rollback-journal mode blocks readers
///   while a writer is active (and vice versa) — a real problem for a desktop app whose
///   UI reads chat/document history while a background feed processor writes
///   concurrently. WAL lets readers proceed against the last committed snapshot while a
///   writer is active. It's a database-level property, persisted in the file after the
///   first connection sets it — applying it on every open is idempotent, not wasted work.</item>
/// <item><c>busy_timeout</c>: WAL narrows lock collisions but doesn't eliminate them —
///   two writers (e.g. two feed processor cycles, or a write racing a checkpoint) still
///   serialize at the file level. Without a busy timeout, a collision throws
///   <c>SQLITE_BUSY</c> immediately instead of waiting a bounded time for the lock to
///   clear.</item>
/// </list>
/// <para>
/// <b>Caveat for the "copy the file to export/move a project" use case:</b> WAL mode
/// keeps uncommitted-but-checkpointed pages in sidecar <c>-wal</c>/<c>-shm</c> files next
/// to the main database file. A raw file copy of just the <c>.db</c> file, taken while a
/// connection is open, can miss data still sitting in the WAL. Checkpoint
/// (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>) or ensure all connections to the file are
/// closed before copying it for export/backup.
/// </para>
/// <para>
/// <b>Cancellation is checked before a command runs, not while it waits.</b>
/// Microsoft.Data.Sqlite runs commands synchronously behind its <c>*Async</c> methods: a
/// <see cref="CancellationToken"/> that fires while a statement runs, or while it waits for another
/// writer's lock, is not observed until the call returns. A running statement ends with its own completion,
/// a wait for a lock with <c>SQLITE_BUSY</c> after the <c>busy_timeout</c> below (5 s).
/// </para>
/// <para>
/// <b>The command timeout is the busy timeout.</b> Microsoft.Data.Sqlite retries a statement that got
/// <c>SQLITE_BUSY</c> until its command timeout, 30 s by default, on top of the engine's busy handler:
/// a write blocked by a session took about 31 s to fail (measured), a frozen UI in a desktop app. Every
/// connection therefore gets <see cref="SqliteConnection.DefaultTimeout"/> = 5 s, unless the connection
/// string sets <c>Default Timeout</c> (or <c>Command Timeout</c>) itself.
/// </para>
/// </remarks>
internal static class SqliteConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int CommandTimeoutSeconds = BusyTimeoutMilliseconds / 1000;

    /// <summary>
    /// Opens a connection and applies the kernel's PRAGMAs. Use this instead of
    /// <c>new SqliteConnection(...)</c> + <c>OpenAsync</c> everywhere in this project.
    /// </summary>
    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(connectionString);
        if (!SetsCommandTimeout(connectionString))
        {
            connection.DefaultTimeout = CommandTimeoutSeconds;
        }

        try
        {
            await connection.OpenAsync(ct);
            await ApplyPragmasAsync(connection, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // DbConnectionStringBuilder (not SqliteConnectionStringBuilder, which reports every known keyword as present)
    // holds only the keys the string names.
    private static bool SetsCommandTimeout(string connectionString)
    {
        var given = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        return given.ContainsKey("Default Timeout") || given.ContainsKey("Command Timeout");
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = 'WAL';";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
