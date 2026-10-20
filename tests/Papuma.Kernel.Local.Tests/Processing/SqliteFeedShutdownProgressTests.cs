// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// A shutdown in the middle of a batch keeps what was already delivered (review P-3): the
/// outcome of phase 3 (checkpoint, cleared failures) is written even though the handlers
/// phase ended in a cancellation; the record it was cancelled on is not counted and comes
/// again. A database file of its own per test — feed state is global per file.
/// </summary>
public sealed class SqliteFeedShutdownProgressTests : IAsyncLifetime
{
    private const int Records = 10;

    private readonly SqliteFixture _db = new();
    private SqliteDocumentStore _store = null!;

    private sealed record ShutdownDoc(string Id, string Name);

    private sealed record ShutdownEvent(string Note);

    // Retries at once: the backoff is a timestamp, and these tests are about shutdown.
    private static readonly ChangeFeedProcessorOptions FastRetry = new()
    {
        BaseRetryDelay = TimeSpan.Zero,
        MaxRetryDelay = TimeSpan.Zero,
    };

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = await _db.CreateStoreAsync(new KernelModelBuilder().Document<ShutdownDoc>().Event<ShutdownEvent>().Build());
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private async Task<List<string>> WriteDocsAsync()
    {
        var ids = new List<string>();
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        for (var i = 0; i < Records; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            await session.SaveAsync(new ShutdownDoc(id, "doc"), 0);
            ids.Add(id);
        }

        await session.CommitAsync();
        return ids;
    }

    private async Task<List<string>> WriteEventsAsync()
    {
        var notes = new List<string>();
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        for (var i = 0; i < Records; i++)
        {
            var note = Guid.NewGuid().ToString("N");
            await session.AppendAsync(new ShutdownEvent(note));
            notes.Add(note);
        }

        await session.CommitAsync();
        return notes;
    }

    /// <summary>
    /// Records every delivery. At the <c>cancelAt</c>-th call (1-based) it cancels the shutdown
    /// token; <c>finishFirst</c> decides whether that call still completes or is interrupted.
    /// Fails the <c>failAt</c>-th call (1-based) once.
    /// </summary>
    private sealed class ShutdownChangeHandler(CancellationTokenSource shutdown, int cancelAt, bool finishFirst, int failAt = 0)
        : IChangeHandler
    {
        private int _calls;

        public string Name { get; } = $"shutdown-{Guid.NewGuid():N}";

        public List<string> Delivered { get; } = [];

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Delivered.Add(change.DocumentId);
            var call = ++_calls;
            if (call == failAt)
            {
                throw new InvalidOperationException("simulated failure");
            }

            if (call == cancelAt)
            {
                shutdown.Cancel();
                if (!finishFirst)
                {
                    ct.ThrowIfCancellationRequested();
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ShutdownEventHandler(CancellationTokenSource shutdown, int cancelAt, bool finishFirst) : IEventHandler
    {
        private int _calls;

        public string Name { get; } = $"shutdown-evt-{Guid.NewGuid():N}";

        public List<string> Delivered { get; } = [];

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            Delivered.Add(@event.Deserialize<ShutdownEvent>().Note);
            if (++_calls == cancelAt)
            {
                shutdown.Cancel();
                if (!finishFirst)
                {
                    ct.ThrowIfCancellationRequested();
                }
            }

            return Task.CompletedTask;
        }
    }

    private SqliteChangeFeedProcessor ChangeProcessor(IChangeHandler handler) =>
        new(_db.ConnectionString, [handler], notifier: null, FastRetry);

    [Fact]
    public async Task ChangeFeed_ShutdownBetweenRecords_KeepsTheRecordsDelivered()
    {
        var ids = await WriteDocsAsync();
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 3, finishFirst: true);
        using var processor = ChangeProcessor(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Equal(ids.Take(3), handler.Delivered);

        handler.Delivered.Clear();
        Assert.Equal(Records - 3, await processor.ProcessOnceAsync());
        Assert.Equal(ids.Skip(3), handler.Delivered); // 1-3 are not delivered again
    }

    [Fact]
    public async Task ChangeFeed_ShutdownMidHandler_KeepsTheEarlierRecords_AndRedeliversTheInterruptedOne()
    {
        var ids = await WriteDocsAsync();
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 4, finishFirst: false);
        using var processor = ChangeProcessor(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Equal(ids.Take(4), handler.Delivered); // the 4th was started, not finished

        handler.Delivered.Clear();
        Assert.Equal(Records - 3, await processor.ProcessOnceAsync());
        Assert.Equal(ids.Skip(3), handler.Delivered); // 1-3 kept, 4 redelivered
        Assert.Empty(await processor.GetFailuresAsync()); // a shutdown is no failure
    }

    [Fact]
    public async Task ChangeFeed_ShutdownOnTheFirstRecord_MovesNothing_AndStillCancels()
    {
        var ids = await WriteDocsAsync();
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 1, finishFirst: false);
        using var processor = ChangeProcessor(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));

        handler.Delivered.Clear();
        Assert.Equal(Records, await processor.ProcessOnceAsync());
        Assert.Equal(ids, handler.Delivered);
    }

    [Fact]
    public async Task ChangeFeed_ShutdownAfterARetriedRecordSucceeded_ClearsItsFailureEntry()
    {
        var ids = await WriteDocsAsync();
        using var shutdown = new CancellationTokenSource();
        // Call 1 fails (first record, attempt 1); call 2 is the retry and finishes with the shutdown.
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 2, finishFirst: true, failAt: 1);
        using var processor = ChangeProcessor(handler);

        await processor.ProcessOnceAsync();
        Assert.Single(await processor.GetFailuresAsync());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Empty(await processor.GetFailuresAsync());

        handler.Delivered.Clear();
        Assert.Equal(Records - 1, await processor.ProcessOnceAsync());
        Assert.Equal(ids.Skip(1), handler.Delivered);
    }

    [Fact]
    public async Task EventFeed_ShutdownMidHandler_KeepsTheEarlierRecords_AndRedeliversTheInterruptedOne()
    {
        var notes = await WriteEventsAsync();
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownEventHandler(shutdown, cancelAt: 4, finishFirst: false);
        using var processor = new SqliteEventFeedProcessor(_db.ConnectionString, [handler], notifier: null, FastRetry);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Equal(notes.Take(4), handler.Delivered);

        handler.Delivered.Clear();
        Assert.Equal(Records - 3, await processor.ProcessOnceAsync());
        Assert.Equal(notes.Skip(3), handler.Delivered);
    }
}
