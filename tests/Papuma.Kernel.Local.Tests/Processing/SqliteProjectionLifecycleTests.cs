// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// Parity with <c>ProjectionLifecycleTests</c> (ADR-024) on the SQLite kernel — with a
/// projection whose table lives in the same database file, so its reset writes to the
/// file whose single write lock the processor must not be holding.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteProjectionLifecycleTests : IAsyncLifetime
{
    private static readonly ChangeFeedProcessorOptions WholeBacklog = new() { BatchSize = 100_000 };

    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteProjectionLifecycleTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record LocalLifecycleDoc(string Id, string Name);

    public async Task InitializeAsync()
    {
        _store = await _fixture.CreateStoreAsync(new KernelModelBuilder().Document<LocalLifecycleDoc>().Build());
        await using var conn = await SqliteConnectionFactory.OpenAsync(_fixture.ConnectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS lifecycle_target (handler TEXT NOT NULL, doc_id TEXT NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewName() => $"lifecycle_{Guid.NewGuid():N}";

    /// <summary>A projection into a table of the same database file, through its own connection.</summary>
    private sealed class FileProjection(string connectionString, string name, int version) : IChangeHandler, IProjection
    {
        public string Name => name;

        public int Version => version;

        public int Resets { get; private set; }

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType != nameof(LocalLifecycleDoc))
            {
                return;
            }

            await using var conn = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO lifecycle_target (handler, doc_id) VALUES (@handler, @id)";
            cmd.Parameters.AddWithValue("handler", name);
            cmd.Parameters.AddWithValue("id", change.DocumentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task ResetAsync(CancellationToken ct)
        {
            await using var conn = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM lifecycle_target WHERE handler = @handler";
            cmd.Parameters.AddWithValue("handler", name);
            await cmd.ExecuteNonQueryAsync(ct);
            Resets++;
        }
    }

    [StartsAtFeedHead]
    private sealed class HeadEffect(string name) : IChangeHandler
    {
        public string Name => name;

        public ConcurrentQueue<string> Acted { get; } = new();

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType == nameof(LocalLifecycleDoc))
            {
                Acted.Enqueue(change.DocumentId);
            }

            return Task.CompletedTask;
        }
    }

    private async Task<List<string>> WriteAsync(int count)
    {
        var ids = new List<string>();
        await using var session = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            await session.SaveAsync(new LocalLifecycleDoc(id, "doc"), 0);
            ids.Add(id);
        }

        await session.CommitAsync();
        return ids;
    }

    private static async Task DrainAsync(SqliteChangeFeedProcessor processor)
    {
        while (await processor.ProcessOnceAsync() > 0)
        {
        }
    }

    private async Task DrainAsync(IChangeHandler handler)
    {
        using var processor = new SqliteChangeFeedProcessor(_fixture.ConnectionString, [handler], notifier: null, WholeBacklog);
        await DrainAsync(processor);
    }

    private async Task<List<string>> TargetAsync(string handler)
    {
        await using var conn = await SqliteConnectionFactory.OpenAsync(_fixture.ConnectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT doc_id FROM lifecycle_target WHERE handler = @handler";
        cmd.Parameters.AddWithValue("handler", handler);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task VersionBump_ResetsATableInTheSameFile_AndReplaysOnce()
    {
        var name = NewName();
        var ids = await WriteAsync(2);
        await DrainAsync(new FileProjection(_fixture.ConnectionString, name, version: 1));
        var built = (await TargetAsync(name)).Order().ToList(); // the file's whole history
        Assert.Superset(ids.ToHashSet(), built.ToHashSet());

        var bumped = new FileProjection(_fixture.ConnectionString, name, version: 2);
        await DrainAsync(bumped);

        Assert.Equal(1, bumped.Resets);
        Assert.Equal(built, (await TargetAsync(name)).Order()); // rebuilt, not doubled

        var again = new FileProjection(_fixture.ConnectionString, name, version: 2);
        await DrainAsync(again);
        Assert.Equal(0, again.Resets);
        Assert.Equal(built, (await TargetAsync(name)).Order());
    }

    [Fact]
    public async Task FirstStartWithAStoredCheckpoint_RecordsTheVersion_WithoutRebuild()
    {
        var name = NewName();
        await WriteAsync(1);
        using (var plain = new SqliteChangeFeedProcessor(
            _fixture.ConnectionString, [new PlainHandler(name)], notifier: null, WholeBacklog))
        {
            await DrainAsync(plain); // a checkpoint from before ADR-024: no version
        }

        var projection = new FileProjection(_fixture.ConnectionString, name, version: 4);
        await DrainAsync(projection);

        Assert.Equal(0, projection.Resets);
        Assert.Empty(await TargetAsync(name)); // no replay
    }

    [Fact]
    public async Task ResetProjectionsAsync_RebuildsTheProjection()
    {
        var name = NewName();
        var ids = await WriteAsync(1);
        var projection = new FileProjection(_fixture.ConnectionString, name, version: 1);
        using var processor = new SqliteChangeFeedProcessor(_fixture.ConnectionString, [projection], notifier: null, WholeBacklog);
        await DrainAsync(processor);
        var built = (await TargetAsync(name)).Order().ToList();
        Assert.Contains(ids[0], built);

        Assert.Equal(1, await processor.ResetProjectionsAsync());
        await DrainAsync(processor);

        Assert.Equal(1, projection.Resets);
        Assert.Equal(built, (await TargetAsync(name)).Order()); // rebuilt, not doubled
    }

    [Fact]
    public async Task OlderVersion_IsPaused()
    {
        var name = NewName();
        await DrainAsync(new FileProjection(_fixture.ConnectionString, name, version: 3));
        var ids = await WriteAsync(1);

        using var older = new SqliteChangeFeedProcessor(
            _fixture.ConnectionString, [new FileProjection(_fixture.ConnectionString, name, version: 2)], notifier: null, WholeBacklog);
        await DrainAsync(older);

        Assert.DoesNotContain(ids[0], await TargetAsync(name));
        Assert.True(Assert.Single(await older.GetLagAsync()).Paused);
    }

    [Fact]
    public async Task EffectStartingAtTheHead_SkipsHistory()
    {
        var history = await WriteAsync(2);
        var effect = new HeadEffect(NewName());
        using var processor = new SqliteChangeFeedProcessor(_fixture.ConnectionString, [effect], notifier: null, WholeBacklog);
        await DrainAsync(processor);

        var fresh = await WriteAsync(1);
        await DrainAsync(processor);

        Assert.Equal(fresh, effect.Acted.ToList());
        Assert.DoesNotContain(history[0], effect.Acted);
    }

    [Fact]
    public async Task SchemaMigration_AddsTheVersionColumn_ToAnOlderFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"papuma_adr024_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            await using (var conn = await SqliteConnectionFactory.OpenAsync(connectionString))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE checkpoint (handler_name TEXT NOT NULL PRIMARY KEY,
                        last_seq INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL)
                    """;
                await cmd.ExecuteNonQueryAsync();
                await SqliteSchemaManager.EnsureSchemaAsync(conn);
                await SqliteSchemaManager.EnsureSchemaAsync(conn); // idempotent

                cmd.CommandText = "SELECT count(*) FROM pragma_table_info('checkpoint') WHERE name = 'projection_version'";
                Assert.Equal(1L, await cmd.ExecuteScalarAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    /// <summary>A plain handler under the name a projection takes over later.</summary>
    private sealed class PlainHandler(string name) : IChangeHandler
    {
        public string Name => name;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }
}
