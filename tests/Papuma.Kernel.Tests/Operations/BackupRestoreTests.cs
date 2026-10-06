// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;

using Testcontainers.PostgreSql;

namespace Papuma.Kernel.Tests.Operations;

/// <summary>
/// The backup and restore recipe (<c>docs/recipes/backup-restore.md</c>) end to end:
/// real <c>pg_dump</c> in one cluster, real <c>pg_restore</c> into a second, independent
/// cluster, then the application starts against the copy.
/// </summary>
public sealed class BackupRestoreTests
{
    private const string Image = PapumaTestDatabase.DefaultImage;
    private const string DumpPath = "/tmp/app.dump";
    private const int RestoredPort = 5433;

    private readonly KernelModel _model =
        new KernelModelBuilder().Document<BackupDoc>().Event<BackupEvent>().Build();

    private sealed record BackupDoc(string Id, int Step);

    private sealed record BackupEvent(int Step);

    private sealed class Recorder(string name) : IChangeHandler
    {
        public List<long> Seqs { get; } = [];

        public string Name { get; } = name;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Seqs.Add(change.Seq);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PgDump_PgRestore_IntoAnotherCluster_AppStartsRepairedNothingLost()
    {
        await using var source = new PostgreSqlBuilder(Image).Build();
        await using var target = new PostgreSqlBuilder(Image).Build();
        await Task.WhenAll(source.StartAsync(), target.StartAsync());

        // The source cluster has lived: its transaction id counter is far ahead of the
        // freshly initialized target's — the situation a logical restore must survive.
        await using var sourceDb = NpgsqlDataSource.Create(source.GetConnectionString());
        await Exec(sourceDb, """
            CREATE PROCEDURE burn_xids(n int) LANGUAGE plpgsql AS $$
            BEGIN
                FOR i IN 1..n LOOP PERFORM pg_current_xact_id(); COMMIT; END LOOP;
            END $$;
            """);
        await Exec(sourceDb, "CALL burn_xids(100000)"); // alone: a COMMIT inside needs its own statement

        await SchemaManager.EnsureSchemaAsync(sourceDb, _model);
        var store = new DocumentStore(sourceDb, _model);
        var delivered = await WriteAsync(store, 3);

        const string Handler = "backup-recipe";
        using (var processor = new ChangeFeedProcessor(sourceDb, [new Recorder(Handler)]))
        {
            await processor.DrainAsync();
        }

        var pending = await WriteAsync(store, 2); // committed, not yet delivered when the backup runs

        // The recipe's backup: the whole database, custom format.
        var dump = await source.ExecAsync(["pg_dump", "-Fc", "-U", "postgres", "-d", "postgres", "-f", DumpPath]);
        Assert.Equal(0, dump.ExitCode);
        await target.CopyAsync(await source.ReadFileAsync(DumpPath), DumpPath);

        // The recipe's restore: an empty database, no owner mapping.
        var create = await target.ExecAsync(["createdb", "-U", "postgres", "restored"]);
        Assert.Equal(0, create.ExitCode);
        var restore = await target.ExecAsync(
            ["pg_restore", "-U", "postgres", "-d", "restored", "--no-owner", DumpPath]);
        Assert.Equal(0, restore.ExitCode);

        var restoredConnection = new NpgsqlConnectionStringBuilder(target.GetConnectionString()) { Database = "restored" };
        await using var restoredDb = NpgsqlDataSource.Create(restoredConnection.ConnectionString);

        // Application start: the restored ids are foreign to this cluster; EnsureSchemaAsync repairs once.
        Assert.True(await SchemaManager.RepairFeedAfterLogicalRestoreAsync(restoredDb));
        await SchemaManager.EnsureSchemaAsync(restoredDb, _model);
        Assert.False(await SchemaManager.RepairFeedAfterLogicalRestoreAsync(restoredDb));

        var fresh = await WriteAsync(new DocumentStore(restoredDb, _model), 2);
        var recorder = new Recorder(Handler);
        using (var processor = new ChangeFeedProcessor(restoredDb, [recorder]))
        {
            await processor.DrainAsync();
        }

        // Everything undelivered at backup time and everything written afterwards arrives,
        // in order; rows delivered before the backup may come again (at-least-once).
        var expected = pending.Concat(fresh).ToList();
        Assert.Equal(expected, recorder.Seqs.Where(expected.Contains).ToList());
        Assert.All(recorder.Seqs, seq => Assert.Contains(seq, delivered.Concat(expected)));

        // The documents themselves are intact.
        Assert.Equal(delivered.Count + pending.Count + fresh.Count, await CountAsync(restoredDb, "papuma.document"));
    }

    [Fact]
    public async Task IncrementalBasebackup_Combined_StartsWithoutRepair_AndDeliversExactlyTheUndelivered()
    {
        // Incremental physical backups (PostgreSQL 17+) need WAL summarization on the server.
        await using var server = new PostgreSqlBuilder(Image)
            .WithCommand("-c", "summarize_wal=on")
            .WithPortBinding(RestoredPort, assignRandomHostPort: true)
            .Build();
        await server.StartAsync();

        await using var db = NpgsqlDataSource.Create(server.GetConnectionString());
        await SchemaManager.EnsureSchemaAsync(db, _model);
        var store = new DocumentStore(db, _model);
        await WriteAsync(store, 3);

        const string Handler = "backup-recipe";
        using (var processor = new ChangeFeedProcessor(db, [new Recorder(Handler)]))
        {
            await processor.DrainAsync();
        }

        // The recipe's chain: a full base backup, later an incremental one on top of it.
        await AsPostgres(server, "pg_basebackup -U postgres -D /tmp/full -c fast");
        var pending = await WriteAsync(store, 2);
        await AsPostgres(
            server, "pg_basebackup -U postgres -D /tmp/incr -c fast -i /tmp/full/backup_manifest");

        // The recipe's restore: combine the chain, start a server on the result.
        await AsPostgres(server, "pg_combinebackup /tmp/full /tmp/incr -o /tmp/combined && chmod 700 /tmp/combined");
        await AsPostgres(
            server,
            $"pg_ctl -D /tmp/combined -o '-p {RestoredPort} -c listen_addresses=*' -w -l /tmp/combined.log start");

        var restored = new NpgsqlConnectionStringBuilder(server.GetConnectionString())
        {
            Host = server.Hostname,
            Port = server.GetMappedPublicPort(RestoredPort),
        };
        await using var restoredDb = NpgsqlDataSource.Create(restored.ConnectionString);

        // Same cluster identity: transaction ids are valid, nothing to repair.
        Assert.False(await SchemaManager.RepairFeedAfterLogicalRestoreAsync(restoredDb));
        await SchemaManager.EnsureSchemaAsync(restoredDb, _model);

        var recorder = new Recorder(Handler);
        using (var processor = new ChangeFeedProcessor(restoredDb, [recorder]))
        {
            await processor.DrainAsync();
        }

        // The checkpoint came with the full backup: only what was written before the
        // incremental backup and not yet delivered arrives — exactly once.
        Assert.Equal(pending, recorder.Seqs);
    }

    private static async Task AsPostgres(PostgreSqlContainer container, string command)
    {
        var result = await container.ExecAsync(["su", "postgres", "-c", command]);
        Assert.True(result.ExitCode == 0, $"{command}: {result.Stderr}");
    }

    private static async Task<List<long>> WriteAsync(DocumentStore store, int count)
    {
        var seqs = new List<long>();
        for (var i = 0; i < count; i++)
        {
            await using var session = store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
            await session.SaveAsync(new BackupDoc(Guid.NewGuid().ToString("N"), i), 0);
            await session.AppendAsync(new BackupEvent(i));
            seqs.Add(Assert.Single(await session.GetChangesByCorrelationAsync(session.CorrelationId)).Seq);
            await session.CommitAsync();
        }

        return seqs;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlDataSource db, string table)
    {
        await using var cmd = db.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
