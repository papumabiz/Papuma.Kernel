// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

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
/// Interleaved transactions (jejak feedback F-15): an older transaction that writes again
/// after a newer one has drawn a sequence number, and commits first, must not carry the
/// checkpoint past the newer transaction's still-invisible rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedGapTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public FeedGapTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record GapDoc(string Id, string Name);

    private sealed record GapEvent(string Name);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<GapDoc>().Event<GapEvent>().Build();
        _store = await _fixture.Database.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private sealed class ChangeRecorder : IChangeHandler
    {
        public ConcurrentDictionary<long, bool> Seen { get; } = new();

        public string Name { get; } = $"gap-{Guid.NewGuid():N}";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Seen[change.Seq] = true;
            return Task.CompletedTask;
        }
    }

    private sealed class EventRecorder : IEventHandler
    {
        public ConcurrentDictionary<long, bool> Seen { get; } = new();

        public string Name { get; } = $"gap-{Guid.NewGuid():N}";

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            Seen[@event.Seq] = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ChangeFeed_DeliversTheNewerTransaction_InterleavedWithAnOlderOne()
    {
        var recorder = new ChangeRecorder();
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [recorder]);
        await processor.DrainAsync(); // start at the head

        await using var a = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        await using var b = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        await a.SaveAsync(new GapDoc(NewId(), "a1"), 0); // A takes the older transaction id
        await b.SaveAsync(new GapDoc(NewId(), "b1"), 0); // B: newer id, next sequence number
        await a.SaveAsync(new GapDoc(NewId(), "a2"), 0); // A writes again, after B
        var bSeq = Assert.Single(await b.GetChangesByCorrelationAsync(b.CorrelationId)).Seq;
        await a.CommitAsync();

        await processor.DrainAsync(); // B still open: A's changes may flow, B's must stay pending
        await b.CommitAsync();
        await processor.DrainAsync();

        Assert.True(recorder.Seen.ContainsKey(bSeq), $"Change {bSeq} of the newer transaction was never delivered.");
    }

    [Fact]
    public async Task EventFeed_DeliversTheNewerTransaction_InterleavedWithAnOlderOne()
    {
        var recorder = new EventRecorder();
        using var processor = new EventFeedProcessor(_fixture.Database.AppDataSource, [recorder]);
        await processor.DrainAsync();

        await using var a = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        await using var b = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        await a.AppendAsync(new GapEvent("a1"));
        var bSeq = await b.AppendAsync(new GapEvent("b1"));
        await a.AppendAsync(new GapEvent("a2"));
        await a.CommitAsync();

        await processor.DrainAsync();
        await b.CommitAsync();
        await processor.DrainAsync();

        Assert.True(recorder.Seen.ContainsKey(bSeq), $"Event {bSeq} of the newer transaction was never delivered.");
    }
}
