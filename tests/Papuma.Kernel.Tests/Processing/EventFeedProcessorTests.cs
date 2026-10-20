// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// Integration tests for the event feed engine's delivery machinery — the counterpart of
/// <see cref="ChangeFeedProcessorTests"/>: poison, retry, lag, leader coordination and
/// the raw checkpoint reset, all against the <c>event:</c>-prefixed checkpoint space.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EventFeedProcessorTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public EventFeedProcessorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record FeedEvent(string Marker);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Event<FeedEvent>().Build();
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewMarker() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private static string NewHandlerName() => $"h_{Guid.NewGuid():N}";

    private sealed class RecordingHandler(string name) : IEventHandler
    {
        public ConcurrentQueue<EventRecord> Received { get; } = new();

        public HashSet<string> FailingMarkers { get; } = [];

        public string Name => name;

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            var marker = @event.Deserialize<FeedEvent>().Marker;
            if (FailingMarkers.Contains(marker))
            {
                throw new InvalidOperationException($"Simulated failure for {marker}.");
            }

            Received.Enqueue(@event);
            return Task.CompletedTask;
        }

        public bool Saw(string marker) => Received.Any(e => e.Deserialize<FeedEvent>().Marker == marker);
    }

    // A fresh handler starts behind every other test's events in the shared database.
    private EventFeedProcessor CreateProcessor(IEventHandler handler, ChangeFeedProcessorOptions? options = null) =>
        new(_fixture.DataSource, [handler], options ?? new ChangeFeedProcessorOptions { BatchSize = 100_000 });

    private async Task AppendAsync(params string[] markers)
    {
        await using var session = _store.OpenSession(NewTenant());
        foreach (var marker in markers)
        {
            await session.AppendAsync(new FeedEvent(marker));
        }

        await session.CommitAsync();
    }

    [Fact]
    public async Task Poison_AfterMaxAttempts_IsSkipped_FailureStaysRecorded_LaterEventsFlow()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var poison = NewMarker();
        var healthy = NewMarker();
        handler.FailingMarkers.Add(poison);

        using var processor = CreateProcessor(handler, new ChangeFeedProcessorOptions
        {
            MaxAttempts = 2,
            BatchSize = 100_000,
            BaseRetryDelay = TimeSpan.Zero,
        });
        await AppendAsync(poison, healthy);

        // Attempt 1, attempt 2 (→ poison), skip cycle: the event behind the poison flows
        // once it is skipped (stop-the-line until then).
        for (var i = 0; i < 4; i++)
        {
            await processor.ProcessOnceAsync();
        }

        Assert.False(handler.Saw(poison));
        Assert.True(handler.Saw(healthy));

        var failure = Assert.Single(await processor.GetFailuresAsync());
        Assert.Equal(handler.Name, failure.HandlerName); // without the internal "event:" prefix
        Assert.Equal(2, failure.Attempts);
        Assert.Contains("Simulated failure", failure.LastError);
        Assert.Equal(1, await CountFailureRowsAsync("event:" + handler.Name));
        Assert.Equal(0, await CountFailureRowsAsync(handler.Name));
    }

    [Fact]
    public async Task RetryFailure_RemovesTheEntry_SoTheNextCycleRetriesAtOnce()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var flaky = NewMarker();
        handler.FailingMarkers.Add(flaky);

        using var processor = CreateProcessor(handler, new ChangeFeedProcessorOptions
        {
            BatchSize = 100_000,
            BaseRetryDelay = TimeSpan.FromHours(1), // the retry is far away — only a manual retry helps
            MaxRetryDelay = TimeSpan.FromHours(1),
        });
        await AppendAsync(flaky);

        await processor.ProcessOnceAsync(); // fails, backs off for an hour
        handler.FailingMarkers.Clear();
        await processor.ProcessOnceAsync();
        Assert.False(handler.Saw(flaky)); // still waiting out the backoff

        var failure = Assert.Single(await processor.GetFailuresAsync());
        Assert.True(await processor.RetryFailureAsync(handler.Name, failure.Seq));
        Assert.False(await processor.RetryFailureAsync(handler.Name, failure.Seq)); // nothing left to remove

        await processor.ProcessOnceAsync();
        Assert.True(handler.Saw(flaky));
        Assert.Empty(await processor.GetFailuresAsync());
    }

    [Fact]
    public async Task RetryFailureAndResetCheckpoint_RejectUnknownAndPrefixedNames()
    {
        var handler = new RecordingHandler(NewHandlerName());
        using var processor = CreateProcessor(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => processor.RetryFailureAsync("no-such-handler", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => processor.RetryFailureAsync("event:" + handler.Name, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => processor.ResetCheckpointAsync("no-such-handler"));
        await Assert.ThrowsAsync<ArgumentException>(() => processor.ResetCheckpointAsync("event:" + handler.Name));
    }

    [Fact]
    public async Task ResetCheckpoint_ReplaysTheFeed_AndOnlyTouchesTheEventCheckpoint()
    {
        var handler = new RecordingHandler(NewHandlerName());
        using var processor = CreateProcessor(handler);
        var marker = NewMarker();
        await AppendAsync(marker);

        await processor.ProcessOnceAsync();
        Assert.Equal(1, handler.Received.Count(e => e.Deserialize<FeedEvent>().Marker == marker));

        await processor.ResetCheckpointAsync(handler.Name);
        await processor.ProcessOnceAsync();

        Assert.Equal(2, handler.Received.Count(e => e.Deserialize<FeedEvent>().Marker == marker));
        Assert.Equal(1, await CountCheckpointRowsAsync("event:" + handler.Name));
        Assert.Equal(0, await CountCheckpointRowsAsync(handler.Name));
    }

    [Fact]
    public async Task Lag_ReflectsPendingEvents_AndDrainsToZero()
    {
        var handler = new RecordingHandler(NewHandlerName());
        using var processor = CreateProcessor(handler);
        await processor.ProcessOnceAsync(); // register + drain pre-existing events

        await AppendAsync(NewMarker(), NewMarker());

        var before = Assert.Single(await processor.GetLagAsync());
        Assert.Equal(handler.Name, before.HandlerName);
        Assert.Equal(2, before.Lag);

        await processor.ProcessOnceAsync();

        var after = Assert.Single(await processor.GetLagAsync());
        Assert.Equal(0, after.Lag);
    }

    [Fact]
    public async Task LeaderCoordination_LockedCheckpoint_IsSkippedByOtherInstances()
    {
        var handler = new RecordingHandler(NewHandlerName());
        using var processor = CreateProcessor(handler);
        await processor.ProcessOnceAsync(); // ensure the checkpoint row exists

        var marker = NewMarker();
        await AppendAsync(marker);

        // A second instance holds the handler's checkpoint lock (the prefixed key).
        await using (var conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = tx;
                lockCmd.CommandText = "SELECT last_seq FROM papuma.checkpoint WHERE handler_name = @name FOR UPDATE";
                lockCmd.Parameters.AddWithValue("name", "event:" + handler.Name);
                await lockCmd.ExecuteScalarAsync();
            }

            Assert.Equal(0, await processor.ProcessOnceAsync()); // skipped, no blocking, no double delivery
        }

        await processor.ProcessOnceAsync();
        Assert.True(handler.Saw(marker));
    }

    private async Task<long> CountFailureRowsAsync(string key)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM papuma.failure WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", key);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountCheckpointRowsAsync(string key)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM papuma.checkpoint WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", key);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
