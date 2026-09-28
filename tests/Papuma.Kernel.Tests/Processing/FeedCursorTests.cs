// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// The snapshot cursor (ADR-022) under concurrency, and its state transitions: lag,
/// migration from a sequence checkpoint, repair after a logical restore.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedCursorTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly KernelModel _model =
        new KernelModelBuilder().Document<CursorDoc>().Event<CursorEvent>().Build();

    private DocumentStore _store = null!;

    public FeedCursorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record CursorDoc(string Id, int Step);

    private sealed record CursorEvent(int Step);

    public async Task InitializeAsync()
    {
        _store = await _fixture.Database.CreateStoreAsync(_model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Records every delivery, in delivery order, of this test's document type.</summary>
    private sealed class ChangeRecorder : IChangeHandler
    {
        public ConcurrentQueue<ChangeRecord> Received { get; } = new();

        public string Name { get; } = $"cursor-{Guid.NewGuid():N}";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType == nameof(CursorDoc))
            {
                Received.Enqueue(change);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class EventRecorder : IEventHandler
    {
        public ConcurrentQueue<long> Received { get; } = new();

        public string Name { get; } = $"cursor-{Guid.NewGuid():N}";

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            if (@event.EventType == nameof(CursorEvent))
            {
                Received.Enqueue(@event.Seq);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Stress_InterleavedWriters_EveryCommittedRowDeliveredOnce_NoneRolledBack_PerDocumentInOrder()
    {
        const int Writers = 8;
        const int TransactionsPerWriter = 25;
        const int HotDocuments = 3;

        // A database of its own: hundreds of rows would put every fresh handler of the
        // other tests behind a longer shared backlog.
        await using var db = await ScratchDatabase.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource, _model);
        var store = new DocumentStore(db.DataSource, _model);
        var changes = new ChangeRecorder();
        var events = new EventRecorder();

        var tenant = ScopeContext.Tenant(Guid.NewGuid());
        var committedChanges = new ConcurrentDictionary<long, bool>();
        var hotIds = Enumerable.Range(0, HotDocuments).Select(_ => NewId()).ToArray();
        await using (var setup = store.OpenSession(tenant))
        {
            foreach (var id in hotIds)
            {
                await setup.SaveAsync(new CursorDoc(id, 0), 0);
            }

            var setupSeqs = (await setup.GetChangesByCorrelationAsync(setup.CorrelationId)).Select(c => c.Seq);
            await setup.CommitAsync();
            Mark(committedChanges, setupSeqs.ToList());
        }

        var committedEvents = new ConcurrentDictionary<long, bool>();
        var rolledBackChanges = new ConcurrentDictionary<long, bool>();
        var rolledBackEvents = new ConcurrentDictionary<long, bool>();

        var small = new ChangeFeedProcessorOptions { BatchSize = 3 };
        using var cts = new CancellationTokenSource();
        var feedLoops = new List<Task>();
        var processors = new List<IDisposable>();
        for (var i = 0; i < 2; i++) // two instances contending for the same handlers
        {
            var changeProcessor = new ChangeFeedProcessor(db.DataSource, [changes], small);
            var eventProcessor = new EventFeedProcessor(db.DataSource, [events], small);
            processors.Add(changeProcessor);
            processors.Add(eventProcessor);
            feedLoops.Add(Loop(changeProcessor.ProcessOnceAsync, cts.Token));
            feedLoops.Add(Loop(eventProcessor.ProcessOnceAsync, cts.Token));
        }

        async Task Writer(int writer)
        {
            var random = new Random(writer);
            var ownIds = Enumerable.Range(0, 3).Select(_ => NewId()).ToArray();
            var ownVersions = new long[ownIds.Length];

            for (var t = 0; t < TransactionsPerWriter; t++)
            {
                var session = store.OpenSession(tenant);
                var versions = (long[])ownVersions.Clone();
                var eventSeqs = new List<long>();
                try
                {
                    // Several writes with pauses in between: other writers' transactions
                    // draw sequence numbers in the gaps (the F-15 interleaving).
                    var writes = random.Next(1, 5);
                    for (var w = 0; w < writes; w++)
                    {
                        var slot = random.Next(ownIds.Length);
                        versions[slot] = (await session.SaveAsync(new CursorDoc(ownIds[slot], t), versions[slot])).Version;
                        if (random.Next(2) == 0)
                        {
                            eventSeqs.Add(await session.AppendAsync(new CursorEvent(t)));
                        }

                        await Task.Delay(random.Next(0, 4));
                    }

                    if (random.Next(3) == 0)
                    {
                        // A contended document: serializes on its row lock, may conflict.
                        var hotId = hotIds[random.Next(hotIds.Length)];
                        var hot = await session.LoadAsync<CursorDoc>(hotId);
                        await session.SaveAsync(new CursorDoc(hotId, t), hot!.Version);
                    }

                    var seqs = (await session.GetChangesByCorrelationAsync(session.CorrelationId)).Select(c => c.Seq).ToList();
                    if (random.Next(5) == 0)
                    {
                        await session.DisposeAsync(); // rollback
                        Mark(rolledBackChanges, seqs);
                        Mark(rolledBackEvents, eventSeqs);
                        continue;
                    }

                    await session.CommitAsync();
                    await session.DisposeAsync();
                    Mark(committedChanges, seqs);
                    Mark(committedEvents, eventSeqs);
                    ownVersions = versions;
                }
                catch (ConcurrencyException)
                {
                    await session.DisposeAsync(); // lost the race on a hot document
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(Writer));
        await cts.CancelAsync();
        await Task.WhenAll(feedLoops);
        processors.ForEach(p => p.Dispose());

        // Everything still pending after the writers finished.
        using (var final = new ChangeFeedProcessor(db.DataSource, [changes], small))
        using (var finalEvents = new EventFeedProcessor(db.DataSource, [events], small))
        {
            await final.DrainAsync(maxCycles: 10_000);
            await finalEvents.DrainAsync(maxCycles: 10_000);
        }

        var delivered = changes.Received.ToList();
        Assert.NotEmpty(rolledBackChanges);
        Assert.Equal(committedChanges.Keys.Order(), delivered.Select(c => c.Seq).Order());
        Assert.DoesNotContain(delivered, c => rolledBackChanges.ContainsKey(c.Seq));
        Assert.Equal(committedEvents.Keys.Order(), events.Received.Order());

        foreach (var document in delivered.GroupBy(c => c.DocumentId))
        {
            var versions = document.Select(c => c.Version).ToList();
            Assert.Equal(versions.Order(), versions); // strictly by version, per document
            Assert.Equal(versions.Count, versions.Distinct().Count());
        }
    }

    [Fact]
    public async Task Lag_CountsCommittedUndeliveredRows_NotOpenTransactions()
    {
        var recorder = new ChangeRecorder();
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [recorder], new() { BatchSize = 100_000 });
        await processor.DrainAsync();

        await using var open = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        await open.SaveAsync(new CursorDoc(NewId(), 1), 0);
        await open.SaveAsync(new CursorDoc(NewId(), 1), 0);
        Assert.Equal(0, Assert.Single(await processor.GetLagAsync()).Lag);

        await using (var committed = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid())))
        {
            await committed.SaveAsync(new CursorDoc(NewId(), 1), 0);
            await committed.CommitAsync();
        }

        Assert.Equal(1, Assert.Single(await processor.GetLagAsync()).Lag);

        await open.CommitAsync();
        Assert.Equal(3, Assert.Single(await processor.GetLagAsync()).Lag);

        await processor.DrainAsync();
        var drained = Assert.Single(await processor.GetLagAsync());
        Assert.Equal(0, drained.Lag);
        Assert.Equal(drained.LatestSeq, drained.Checkpoint);
    }

    [Fact]
    public async Task Migration_FromSequenceCheckpoint_DeliversOnlyWhatLiesAboveIt()
    {
        await using var db = await ScratchDatabase.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource, _model);
        var store = new DocumentStore(db.DataSource, _model);
        var changes = new ChangeRecorder();
        var events = new EventRecorder();

        var before = await WriteAsync(store, 2);
        using (var processor = new ChangeFeedProcessor(db.DataSource, [changes]))
        using (var eventProcessor = new EventFeedProcessor(db.DataSource, [events]))
        {
            await processor.DrainAsync();
            await eventProcessor.DrainAsync();
        }

        // Back to the pre-ADR-022 schema: a sequence checkpoint only.
        await db.ExecuteAsync("""
            ALTER TABLE papuma.checkpoint
                DROP COLUMN base_seq, DROP COLUMN done_snapshot,
                DROP COLUMN slice_snapshot, DROP COLUMN slice_seq;
            DROP INDEX papuma.ix_papuma_change_txid;
            DROP INDEX papuma.ix_papuma_event_txid;
            """);
        var after = await WriteAsync(store, 2);

        await SchemaManager.EnsureSchemaAsync(db.DataSource, _model);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM papuma.checkpoint WHERE base_seq <> last_seq"));

        using (var processor = new ChangeFeedProcessor(db.DataSource, [changes]))
        using (var eventProcessor = new EventFeedProcessor(db.DataSource, [events]))
        {
            await processor.DrainAsync();
            await eventProcessor.DrainAsync();
        }

        Assert.Equal(before.Changes.Concat(after.Changes), changes.Received.Select(c => c.Seq));
        Assert.Equal(before.Events.Concat(after.Events), events.Received);
    }

    [Fact]
    public async Task LogicalRestore_IsRepaired_UnderRowLevelSecurity_UndeliveredRowsAndNewWritesFlow()
    {
        await using var db = await ScratchDatabase.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource, _model);
        var store = new DocumentStore(db.DataSource, _model);
        var changes = new ChangeRecorder();
        var events = new EventRecorder();

        var delivered = await WriteAsync(store, 2);
        using (var processor = new ChangeFeedProcessor(db.DataSource, [changes]))
        using (var eventProcessor = new EventFeedProcessor(db.DataSource, [events]))
        {
            await processor.DrainAsync();
            await eventProcessor.DrainAsync();
        }

        var pending = await WriteAsync(store, 2);

        // A dump from a cluster whose transaction ids ran far ahead of this one: every
        // stored id and cursor snapshot lies beyond the current id counter.
        const long Offset = 50_000_000;
        await db.ExecuteAsync($"""
            UPDATE papuma.change SET txid = (txid::text::bigint + {Offset})::text::xid8;
            UPDATE papuma.event SET txid = (txid::text::bigint + {Offset})::text::xid8;
            """);
        var cursors = await db.QueryAsync("SELECT handler_name, done_snapshot::text FROM papuma.checkpoint");
        foreach (var (name, snapshot) in cursors)
        {
            await db.ExecuteAsync(
                "UPDATE papuma.checkpoint SET done_snapshot = @s::pg_snapshot WHERE handler_name = @n",
                ("s", Shift(snapshot, Offset)), ("n", name));
        }

        // As a plain login role — RLS applies (a superuser would pass even a broken repair).
        await using (var appRole = await db.CreateRestrictedRoleAsync())
        {
            Assert.True(await SchemaManager.RepairFeedAfterLogicalRestoreAsync(appRole));
        }

        await SchemaManager.EnsureSchemaAsync(db.DataSource, _model); // runs the repair too — nothing left
        Assert.False(await SchemaManager.RepairFeedAfterLogicalRestoreAsync(db.DataSource));

        var fresh = await WriteAsync(store, 2);
        using (var processor = new ChangeFeedProcessor(db.DataSource, [changes]))
        using (var eventProcessor = new EventFeedProcessor(db.DataSource, [events]))
        {
            await processor.DrainAsync();
            await eventProcessor.DrainAsync();
        }

        // Nothing delivered twice, nothing lost — before, during and after the restore.
        Assert.Equal(delivered.Changes.Concat(pending.Changes).Concat(fresh.Changes), changes.Received.Select(c => c.Seq));
        Assert.Equal(delivered.Events.Concat(pending.Events).Concat(fresh.Events), events.Received);
    }

    /// <summary>
    /// Runs cycles until <paramref name="stop"/> is signalled — between cycles, never inside
    /// one: a cycle cancelled after its handler ran is rolled back and delivered again
    /// (at-least-once), which would hide a genuine duplicate.
    /// </summary>
    private static async Task Loop(Func<CancellationToken, Task<int>> processOnce, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await processOnce(CancellationToken.None);
        }
    }

    private static void Mark(ConcurrentDictionary<long, bool> set, IEnumerable<long> seqs)
    {
        foreach (var seq in seqs)
        {
            set[seq] = true;
        }
    }

    /// <summary>Commits <paramref name="count"/> single-write transactions, each with one event.</summary>
    private static async Task<(List<long> Changes, List<long> Events)> WriteAsync(DocumentStore store, int count)
    {
        var changes = new List<long>();
        var events = new List<long>();
        for (var i = 0; i < count; i++)
        {
            await using var session = store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
            await session.SaveAsync(new CursorDoc(NewId(), i), 0);
            events.Add(await session.AppendAsync(new CursorEvent(i)));
            changes.Add(Assert.Single(await session.GetChangesByCorrelationAsync(session.CorrelationId)).Seq);
            await session.CommitAsync();
        }

        return (changes, events);
    }

    /// <summary>Moves every transaction id of a <c>pg_snapshot</c> text (<c>xmin:xmax:xip,…</c>) by <paramref name="offset"/>.</summary>
    private static string Shift(string snapshot, long offset)
    {
        var parts = snapshot.Split(':');
        var xip = parts[2].Length == 0
            ? string.Empty
            : string.Join(',', parts[2].Split(',').Select(x => long.Parse(x) + offset));
        return $"{long.Parse(parts[0]) + offset}:{long.Parse(parts[1]) + offset}:{xip}";
    }

    /// <summary>
    /// A database of its own in the shared container, for tests that rewrite feed state
    /// globally (schema downgrades, transaction ids) or write in bulk, and must not touch
    /// other tests' rows.
    /// </summary>
    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly PapumaTestDatabase _server;
        private readonly string _name;
        private readonly List<string> _roles = [];

        private ScratchDatabase(PapumaTestDatabase server, string name, NpgsqlDataSource dataSource)
        {
            _server = server;
            _name = name;
            DataSource = dataSource;
        }

        public NpgsqlDataSource DataSource { get; }

        public static async Task<ScratchDatabase> CreateAsync(PapumaTestDatabase server)
        {
            var name = $"cursor_{Guid.NewGuid():N}";
            await using (var cmd = server.OwnerDataSource.CreateCommand($"CREATE DATABASE {name}"))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(server.OwnerConnectionString) { Database = name };
            return new ScratchDatabase(server, name, NpgsqlDataSource.Create(builder.ConnectionString));
        }

        /// <summary>
        /// A login role of its own (no superuser, not the table owner) with DML on
        /// <c>papuma.*</c> — so RLS applies, as for an application in production.
        /// </summary>
        public async Task<NpgsqlDataSource> CreateRestrictedRoleAsync()
        {
            var role = $"scratch_{Guid.NewGuid():N}";
            var password = Guid.NewGuid().ToString("N");
            await ExecuteAsync($"""
                CREATE ROLE {role} LOGIN PASSWORD '{password}';
                GRANT USAGE ON SCHEMA papuma TO {role};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA papuma TO {role};
                """);
            _roles.Add(role);

            var builder = new NpgsqlConnectionStringBuilder(DataSource.ConnectionString)
            {
                Username = role,
                Password = password,
            };
            return NpgsqlDataSource.Create(builder.ConnectionString);
        }

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var cmd = DataSource.CreateCommand(sql);
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var cmd = DataSource.CreateCommand(sql);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        public async Task<List<(string, string)>> QueryAsync(string sql)
        {
            var rows = new List<(string, string)>();
            await using var cmd = DataSource.CreateCommand(sql);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await using (var drop = _server.OwnerDataSource.CreateCommand($"DROP DATABASE {_name} WITH (FORCE)"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            foreach (var role in _roles) // roles are cluster-wide; their grants went with the database
            {
                await using var dropRole = _server.OwnerDataSource.CreateCommand($"DROP ROLE {role}");
                await dropRole.ExecuteNonQueryAsync();
            }
        }
    }
}
