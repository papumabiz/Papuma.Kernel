// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
/// A shutdown in the middle of a batch keeps what was already delivered (review P-3):
/// the records the handler finished are checkpointed, so the next cycle does not deliver
/// them again; the record it was cancelled on is not counted and comes again.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedShutdownProgressTests
{
    private const int Records = 10;

    private readonly PostgresFixture _fixture;

    public FeedShutdownProgressTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record ShutdownDoc(string Id, string Name);

    private sealed record ShutdownEvent(string Note);

    private static readonly KernelModel Model = new KernelModelBuilder()
        .Document<ShutdownDoc>()
        .Event<ShutdownEvent>()
        .Build();

    // Retries at once: the backoff is the database clock's, and these tests are about shutdown.
    private static readonly ChangeFeedProcessorOptions FastRetry = new()
    {
        BaseRetryDelay = TimeSpan.Zero,
        MaxRetryDelay = TimeSpan.Zero,
    };

    // Feed state is global per database, so every test gets its own.
    private async Task<(ScratchPostgresDatabase Db, DocumentStore Store)> NewDatabaseAsync()
    {
        var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource, Model);
        return (db, new DocumentStore(db.DataSource, Model));
    }

    private static async Task<List<string>> WriteDocsAsync(DocumentStore store)
    {
        var ids = new List<string>();
        await using var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        for (var i = 0; i < Records; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            await session.SaveAsync(new ShutdownDoc(id, "doc"), 0);
            ids.Add(id);
        }

        await session.CommitAsync();
        return ids;
    }

    private static async Task<List<string>> WriteEventsAsync(DocumentStore store)
    {
        var notes = new List<string>();
        await using var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
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
    /// token; <c>finishFirst</c> decides whether that call still completes (shutdown arrives
    /// right after the record was handled) or is interrupted (shutdown arrives mid-flight).
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

    [Fact]
    public async Task ChangeFeed_ShutdownBetweenRecords_KeepsTheRecordsDelivered()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        var ids = await WriteDocsAsync(store);
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 3, finishFirst: true);
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], FastRetry);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Equal(ids.Take(3), handler.Delivered);

        handler.Delivered.Clear();
        Assert.Equal(Records - 3, await processor.ProcessOnceAsync());
        Assert.Equal(ids.Skip(3), handler.Delivered); // 1-3 are not delivered again
    }

    [Fact]
    public async Task ChangeFeed_ShutdownMidHandler_KeepsTheEarlierRecords_AndRedeliversTheInterruptedOne()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        var ids = await WriteDocsAsync(store);
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 4, finishFirst: false);
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], FastRetry);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Equal(ids.Take(4), handler.Delivered); // the 4th was started, not finished

        handler.Delivered.Clear();
        Assert.Equal(Records - 3, await processor.ProcessOnceAsync());
        Assert.Equal(ids.Skip(3), handler.Delivered); // 1-3 kept, 4 redelivered
        Assert.Empty(await processor.GetFailuresAsync()); // a shutdown is no failure
    }

    [Fact]
    public async Task ChangeFeed_ShutdownOnTheFirstRecord_MovesNothing()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        var ids = await WriteDocsAsync(store);
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 1, finishFirst: false);
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], FastRetry);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));

        handler.Delivered.Clear();
        Assert.Equal(Records, await processor.ProcessOnceAsync());
        Assert.Equal(ids, handler.Delivered);
    }

    [Fact]
    public async Task ChangeFeed_ShutdownAfterARetriedRecordSucceeded_ClearsItsFailureEntry()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        var ids = await WriteDocsAsync(store);
        using var shutdown = new CancellationTokenSource();
        // Call 1 fails (first record, attempt 1); call 2 is the retry and finishes with the shutdown.
        var handler = new ShutdownChangeHandler(shutdown, cancelAt: 2, finishFirst: true, failAt: 1);
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], FastRetry);

        await processor.ProcessOnceAsync();
        Assert.Single(await processor.GetFailuresAsync());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Empty(await processor.GetFailuresAsync()); // cleared together with the checkpoint move

        handler.Delivered.Clear();
        Assert.Equal(Records - 1, await processor.ProcessOnceAsync());
        Assert.Equal(ids.Skip(1), handler.Delivered);
    }

    [Fact]
    public async Task EventFeed_ShutdownMidHandler_KeepsTheEarlierRecords_AndRedeliversTheInterruptedOne()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        var notes = await WriteEventsAsync(store);
        using var shutdown = new CancellationTokenSource();
        var handler = new ShutdownEventHandler(shutdown, cancelAt: 4, finishFirst: false);
        using var processor = new EventFeedProcessor(db.DataSource, [handler], FastRetry);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessOnceAsync(shutdown.Token));
        Assert.Equal(notes.Take(4), handler.Delivered);

        handler.Delivered.Clear();
        Assert.Equal(Records - 3, await processor.ProcessOnceAsync());
        Assert.Equal(notes.Skip(3), handler.Delivered);
    }
}
