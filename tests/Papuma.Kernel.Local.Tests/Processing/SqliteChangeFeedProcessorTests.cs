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
