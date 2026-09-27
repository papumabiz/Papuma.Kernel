// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// Integration tests for <see cref="SqliteChangeFeedProcessor"/>: ordering, retry/poison,
/// checkpoint persistence, lag. Mirrors the shape of
/// <c>Papuma.Kernel.Tests.Processing.ChangeFeedProcessorTests</c> minus the Postgres-only
/// concerns (gapless snapshot reads, leader coordination) that don't apply to a
/// single-writer embedded store.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteChangeFeedProcessorTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteChangeFeedProcessorTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record FeedDoc(string Id, string Name);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<FeedDoc>().Build();
        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private static string NewHandlerName() => $"h_{Guid.NewGuid():N}";

    private sealed class RecordingHandler(string name) : IChangeHandler
    {
        public ConcurrentQueue<ChangeRecord> Received { get; } = new();

        public HashSet<string> FailingDocumentIds { get; } = [];

        public string Name => name;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (FailingDocumentIds.Contains(change.DocumentId))
            {
                throw new InvalidOperationException($"Simulated failure for {change.DocumentId}.");
            }

            Received.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private SqliteChangeFeedProcessor CreateProcessor(IChangeHandler handler, ChangeFeedProcessorOptions? options = null) =>
        new(_fixture.ConnectionString, [handler], notifier: null, options);

    /// <summary>Blocks inside HandleAsync until released — to act while a batch is in flight.</summary>
    private sealed class GateHandler(string name) : IChangeHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Armed { get; set; } = true;

        public string Name => name;

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (!Armed)
            {
                return;
            }

            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task HandlerCanWriteToTheSameDatabaseFile_ThroughItsOwnConnection()
    {
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(NewId(), "projected"), 0);
            await session.CommitAsync();
        }

        var table = $"proj_{Guid.NewGuid():N}";
        await using (var setup = new Microsoft.Data.Sqlite.SqliteConnection(_fixture.ConnectionString))
        {
            await setup.OpenAsync();
            await using var ddl = setup.CreateCommand();
            ddl.CommandText = $"CREATE TABLE {table} (seq INTEGER PRIMARY KEY)";
            await ddl.ExecuteNonQueryAsync();
        }

        var handler = new ProjectingHandler(NewHandlerName(), _fixture.ConnectionString, table);
        var processor = CreateProcessor(handler, new ChangeFeedProcessorOptions { MaxAttempts = 1 });

        var delivered = await processor.ProcessOnceAsync();

        Assert.True(delivered > 0);
        Assert.Empty(await processor.GetFailuresAsync());
    }

    private sealed class ProjectingHandler(string name, string connectionString, string table) : IChangeHandler
    {
        public string Name => name;

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT OR IGNORE INTO {table} (seq) VALUES (@seq)";
            cmd.Parameters.AddWithValue("seq", change.Seq);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    [Fact]
    public async Task SlowHandler_DoesNotBlockApplicationWrites()
    {
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(NewId(), "first"), 0);
            await session.CommitAsync();
        }

        var handler = new GateHandler(NewHandlerName());
        var processor = CreateProcessor(handler);
        var cycle = processor.ProcessOnceAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The handler is mid-batch. A write must not wait for it (busy timeout is 5 s).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(NewId(), "while handling"), 0);
            await session.CommitAsync();
        }

        stopwatch.Stop();
        handler.Release.SetResult();
        await cycle;

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Write took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task CheckpointResetDuringABatch_IsNotOverwritten()
    {
        var handler = new GateHandler(NewHandlerName()) { Armed = false };
        var processor = CreateProcessor(handler, new ChangeFeedProcessorOptions { BatchSize = 10_000 });
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(NewId(), "already seen"), 0);
            await session.CommitAsync();
        }

        await processor.ProcessOnceAsync(); // checkpoint moves past everything so far
        Assert.True(Assert.Single(await processor.GetLagAsync()).Checkpoint > 0);

        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(NewId(), "in flight"), 0);
            await session.CommitAsync();
        }

        handler.Armed = true;
        var cycle = processor.ProcessOnceAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await processor.ResetCheckpointAsync(handler.Name); // an operator rebuild, mid-batch
        handler.Release.SetResult();
        var delivered = await cycle;

        Assert.Equal(0, delivered); // outcome of the stale position dropped
        var lag = Assert.Single(await processor.GetLagAsync());
        Assert.Equal(0, lag.Checkpoint); // the reset stands; the next cycle replays
    }

    [Fact]
    public async Task DeliversCommittedChanges_InSeqOrder()
    {
        var scope = NewTenant();
        var id = NewId();
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new FeedDoc(id, "Harry"), 0);
            await session.SaveAsync(new FeedDoc(id, "Harald"), 1);
            await session.CommitAsync();
        }

        var handler = new RecordingHandler(NewHandlerName());
        await CreateProcessor(handler).ProcessOnceAsync();

        var records = handler.Received.Where(r => r.DocumentId == id).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(ChangeOperation.Insert, records[0].Operation);
        Assert.Equal(ChangeOperation.Update, records[1].Operation);
        Assert.True(records[0].Seq < records[1].Seq);
    }

    [Fact]
    public async Task Checkpoint_PersistsAcrossProcessOnceCalls_NoRedelivery()
    {
        // Fixture db is shared across the collection — assert on the processor's return
        // value (records processed *this cycle*) and per-document filtering, not raw
        // handler.Received counts, which would also see other tests' history.
        var id = NewId();
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(id, "A"), 0);
            await session.CommitAsync();
        }

        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler);
        await processor.ProcessOnceAsync();
        var deliveredForThisDoc = handler.Received.Count(r => r.DocumentId == id);

        // Second cycle with no new writes: nothing new to deliver at all.
        var processedSecondCycle = await processor.ProcessOnceAsync();

        Assert.Equal(1, deliveredForThisDoc);
        Assert.Equal(0, processedSecondCycle);
    }

    [Fact]
    public async Task StopsTheLine_OnFailure_AndSkipsAsPoisonAfterMaxAttempts()
    {
        var scope = NewTenant();
        var failingId = NewId();
        var laterId = NewId();
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new FeedDoc(failingId, "boom"), 0);
            await session.SaveAsync(new FeedDoc(laterId, "later"), 0);
            await session.CommitAsync();
        }

        var handler = new RecordingHandler(NewHandlerName());
        handler.FailingDocumentIds.Add(failingId);
        // MaxAttempts=1: the first failure already leaves attempts=1 in the failure
        // table, so the very next cycle sees attempts(1) >= MaxAttempts(1) and poison-skips
        // immediately (item.Attempts is read *before* the attempt — see ProcessHandlerBatchAsync).
        var options = new ChangeFeedProcessorOptions { MaxAttempts = 1, BaseRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero };
        var processor = CreateProcessor(handler, options);

        // Cycle 1: fails on failingId, stop-the-line — laterId (right after it in seq
        // order) never gets a chance in this cycle, regardless of what else the shared
        // fixture db already contains from other tests.
        await processor.ProcessOnceAsync();
        Assert.DoesNotContain(handler.Received, r => r.DocumentId == laterId);

        var failures = await processor.GetFailuresAsync();
        var failure = Assert.Single(failures); // handler name is unique per test — no cross-test entries
        Assert.Equal(1, failure.Attempts);

        // Cycle 2 (MaxAttempts reached): skipped as poison, checkpoint advances past it,
        // laterId is now delivered.
        await processor.ProcessOnceAsync();

        Assert.Contains(handler.Received, r => r.DocumentId == laterId);
    }

    [Fact]
    public async Task GetLagAsync_ReflectsUndeliveredChanges()
    {
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(NewId(), "A"), 0);
            await session.SaveAsync(new FeedDoc(NewId(), "B"), 0);
            await session.CommitAsync();
        }

        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler);

        var lagBefore = (await processor.GetLagAsync()).Single();
        Assert.True(lagBefore.Lag >= 2);

        await processor.ProcessOnceAsync();
        var lagAfter = (await processor.GetLagAsync()).Single();
        Assert.Equal(0, lagAfter.Lag);
    }
}
