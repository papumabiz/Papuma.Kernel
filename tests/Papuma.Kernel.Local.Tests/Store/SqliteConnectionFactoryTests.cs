// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Verifies <see cref="Papuma.Kernel.Store.SqliteConnectionFactory"/> actually applies WAL mode and a busy
/// timeout — empirically, not assumed, matching this project's spike-test discipline
/// (<see cref="SqliteSpikeTests"/>).
/// </summary>
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"papuma_connfactory_test_{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        // WAL leaves sidecar files next to the main one — clean those up too.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = _dbPath + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }

    [Fact]
    public async Task OpenAsync_EnablesWalJournalMode()
    {
        await using var conn = await SqliteConnectionFactory.OpenAsync(ConnectionString);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string)(await cmd.ExecuteScalarAsync())!;

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task OpenAsync_SetsBusyTimeout()
    {
        await using var conn = await SqliteConnectionFactory.OpenAsync(ConnectionString);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        var timeoutMs = (long)(await cmd.ExecuteScalarAsync())!;

        Assert.Equal(5000, timeoutMs);
    }

    [Fact]
    public async Task WalMode_LetsAReaderProceed_WhileAWriteTransactionIsOpen()
    {
        // The concrete problem WAL solves: without it, a reader on a second connection
        // would block (or, without busy_timeout, throw SQLITE_BUSY immediately) while a
        // writer holds an open transaction on another connection to the same file.
        await using var writerConn = await SqliteConnectionFactory.OpenAsync(ConnectionString);
        await using (var createCmd = writerConn.CreateCommand())
        {
            createCmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);";
            await createCmd.ExecuteNonQueryAsync();
        }

        await using var writeTx = await writerConn.BeginTransactionAsync();
        await using (var insertCmd = writerConn.CreateCommand())
        {
            insertCmd.Transaction = (SqliteTransaction)writeTx;
            insertCmd.CommandText = "INSERT INTO t (v) VALUES ('uncommitted');";
            await insertCmd.ExecuteNonQueryAsync();
        }

        // Second, independent connection reads while the write transaction above is
        // still open (not yet committed) — must not throw or hang.
        await using var readerConn = await SqliteConnectionFactory.OpenAsync(ConnectionString);
        await using var readCmd = readerConn.CreateCommand();
        readCmd.CommandText = "SELECT COUNT(*) FROM t;";
        var visibleRows = (long)(await readCmd.ExecuteScalarAsync())!;

        // The reader sees the last *committed* snapshot (0 rows) — WAL's actual
        // guarantee is "readers aren't blocked," not "readers see uncommitted writes."
        Assert.Equal(0, visibleRows);

        await writeTx.CommitAsync();
    }
}
