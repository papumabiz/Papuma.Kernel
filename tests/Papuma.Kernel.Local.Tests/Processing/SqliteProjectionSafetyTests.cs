// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// SQLite parity of <c>ProjectionSafetyTests</c> (review P-4, P-13, P-14) plus the checkpoint
/// generation (L-3): reading never rebuilds, an interrupted manual rebuild is finished on the
/// next start, checkpoint tools accept only their own feed's handlers — and a reset that lands
/// while a batch started from checkpoint 0 is running cannot be overwritten by that batch.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteProjectionSafetyTests : IAsyncLifetime
{
    private static readonly ChangeFeedProcessorOptions WholeBacklog = new() { BatchSize = 100_000 };

    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteProjectionSafetyTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record SafetyDoc(string Id, string Name);

    public async Task InitializeAsync() =>
        _store = await _fixture.CreateStoreAsync(new KernelModelBuilder().Document<SafetyDoc>().Build());

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewName() => $"safety_{Guid.NewGuid():N}";

    /// <summary>
    /// A projection whose target is an in-memory list. <see cref="FailResetOnce"/> makes the next
    /// <c>ResetAsync</c> empty the target and then throw — what a crash in the middle of a rebuild leaves.
    /// <see cref="Gate"/> blocks the first delivery until released, to act while a batch is in flight.
    /// </summary>
    private sealed class DocProjection(string name, int version) : IChangeHandler, IProjection
    {
        private int _failResets;

        public string Name => name;

        public int Version => version;

        public ConcurrentQueue<string> Target { get; } = new();

        public int Resets { get; private set; }

        public TaskCompletionSource? Entered { get; private set; }

        public TaskCompletionSource? Release { get; private set; }

        public void FailResetOnce() => _failResets = 1;

        public void Gate()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType != nameof(SafetyDoc))
            {
                return;
            }

            if (Entered is { } entered && Release is { } release)
            {
                Entered = null; // only the first delivery waits
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            Target.Enqueue(change.DocumentId);
        }

        public Task ResetAsync(CancellationToken ct)
        {
            Target.Clear();
            Resets++;
            if (_failResets > 0)
            {
                _failResets--;
                throw new InvalidOperationException("simulated crash in the middle of a rebuild");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoopEventHandler(string name) : IEventHandler
    {
        public string Name => name;

        public Task HandleAsync(EventRecord @event, CancellationToken ct) => Task.CompletedTask;
    }

    private async Task<List<string>> WriteAsync(int count)
    {
        var ids = new List<string>();
        await using var session = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            await session.SaveAsync(new SafetyDoc(id, "doc"), 0);
            ids.Add(id);
        }

        await session.CommitAsync();
        return ids;
    }

    private SqliteChangeFeedProcessor Processor(params IChangeHandler[] handlers) =>
        new(_fixture.ConnectionString, handlers, notifier: null, WholeBacklog);

    private static async Task DrainAsync(SqliteChangeFeedProcessor processor)
    {
        while (await processor.ProcessOnceAsync() > 0)
        {
        }
    }

    private async Task<long?> ScalarAsync(string sql, string name)
    {
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("name", name);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private Task<long?> StoredVersionAsync(string key) =>
        ScalarAsync("SELECT projection_version FROM checkpoint WHERE handler_name = @name", key);

    private Task<long?> CheckpointAsync(string key) =>
        ScalarAsync("SELECT last_seq FROM checkpoint WHERE handler_name = @name", key);

    // ── P-4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLag_OnANodeThatDeclaresANewerVersion_DoesNotRebuildTheProjection()
    {
        var name = NewName();
        var ids = await WriteAsync(2);
        var old = new DocProjection(name, version: 1);
        using (var worker = Processor(old))
        {
            await DrainAsync(worker);
        }

        Assert.Superset(ids.ToHashSet(), old.Target.ToHashSet());

        var upgraded = new DocProjection(name, version: 2);
        using var webNode = Processor(upgraded);
        await webNode.GetLagAsync();

        Assert.Equal(0, upgraded.Resets);
        Assert.Equal(1L, await StoredVersionAsync(name));
        Assert.Superset(ids.ToHashSet(), old.Target.ToHashSet());
    }

    [Fact]
    public async Task GetLag_OfAnUnregisteredHandler_ShowsTheBacklog_AndWritesNothing()
    {
        var name = NewName();
        await WriteAsync(1);
        using var processor = Processor(new DocProjection(name, version: 1));

        var lag = Assert.Single(await processor.GetLagAsync());

        Assert.True(lag.Lag > 0);
        Assert.Equal(0, lag.Checkpoint);
        Assert.Null(await CheckpointAsync(name)); // no checkpoint row was created by looking
    }

    // ── P-14 / N-2 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManualReset_InterruptedAfterEmptyingTheTarget_IsRepeatedOnTheNextStart()
    {
        var name = NewName();
        var ids = await WriteAsync(3);
        var projection = new DocProjection(name, version: 1);
        using (var worker = Processor(projection))
        {
            await DrainAsync(worker);
            Assert.Superset(ids.ToHashSet(), projection.Target.ToHashSet());

            projection.FailResetOnce();
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ResetProjectionAsync(name));
            Assert.Empty(projection.Target);
        }

        using (var restarted = Processor(projection))
        {
            await DrainAsync(restarted);
        }

        Assert.Superset(ids.ToHashSet(), projection.Target.ToHashSet());
        Assert.Equal(1L, await StoredVersionAsync(name));
    }

    [Fact]
    public async Task VersionRebuild_WhoseResetFailsOnce_IsRetriedAndCompletesOnce()
    {
        var name = NewName();
        var ids = await WriteAsync(2);
        using (var worker = Processor(new DocProjection(name, version: 1)))
        {
            await DrainAsync(worker);
        }

        var upgraded = new DocProjection(name, version: 2);
        upgraded.FailResetOnce();
        using var processor = Processor(upgraded);

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessOnceAsync());
        // Not recorded as rebuilt. On SQLite the target is emptied outside the checkpoint's
        // transaction, so the "rebuild pending" marker is committed first and stands until it is done.
        Assert.Equal(-1L, await StoredVersionAsync(name));

        await DrainAsync(processor);

        Assert.Equal(2, upgraded.Resets);
        Assert.Equal(2L, await StoredVersionAsync(name));
        Assert.Superset(ids.ToHashSet(), upgraded.Target.ToHashSet());
    }

    // ── L-3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetDuringTheFirstBatch_StartedFromCheckpointZero_IsNotOverwrittenByThatBatch()
    {
        var name = NewName();
        await WriteAsync(2);
        var projection = new DocProjection(name, version: 1);
        projection.Gate();
        var entered = projection.Entered!; // the handler clears its own field after the first call
        var release = projection.Release!;
        using var processor = Processor(projection);

        // A fresh handler: the batch starts at checkpoint 0 (backfill) and stops inside a handler.
        var cycle = processor.ProcessOnceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // An operator rebuild lands mid-batch: it empties the target and sets the checkpoint to 0 —
        // "0 to 0", which a comparison of last_seq alone cannot tell from "nothing happened".
        await processor.ResetProjectionAsync(name);
        release.SetResult();
        var delivered = await cycle;

        // The stale outcome must be dropped: before the fix the checkpoint jumped to the end of the
        // batch although the target had just been emptied — the rows were lost for good.
        Assert.Equal(0, delivered);
        Assert.Equal(0L, await CheckpointAsync(name));

        await DrainAsync(processor);
        Assert.NotEmpty(projection.Target); // the next cycles replay everything into the emptied target
    }

    // ── P-13 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("event:orders")]
    [InlineData("a_typo")]
    public async Task ChangeFeed_ResetAndRetry_RejectAHandlerThatIsNotInThisFeed(string handlerName)
    {
        using var processor = Processor(new DocProjection(NewName(), version: 1));

        var reset = await Assert.ThrowsAsync<ArgumentException>(() => processor.ResetCheckpointAsync(handlerName));
        Assert.Equal("handlerName", reset.ParamName);
        var retry = await Assert.ThrowsAsync<ArgumentException>(() => processor.RetryFailureAsync(handlerName, 1));
        Assert.Equal("handlerName", retry.ParamName);
        Assert.Null(await CheckpointAsync(handlerName)); // no orphan row
    }

    [Fact]
    public async Task EventFeed_ResetAndRetry_RejectAHandlerThatIsNotInThisFeed()
    {
        using var processor = new SqliteEventFeedProcessor(
            _fixture.ConnectionString, [new NoopEventHandler(NewName())], notifier: null, WholeBacklog);

        var reset = await Assert.ThrowsAsync<ArgumentException>(() => processor.ResetCheckpointAsync("a_typo"));
        Assert.Equal("handlerName", reset.ParamName);
        var retry = await Assert.ThrowsAsync<ArgumentException>(() => processor.RetryFailureAsync("a_typo", 1));
        Assert.Equal("handlerName", retry.ParamName);
    }

    [Fact]
    public async Task ChangeFeed_ResetAndRetry_StillWorkForItsOwnHandler()
    {
        var projection = new DocProjection(NewName(), version: 1);
        using var processor = Processor(projection);
        await WriteAsync(1);
        await DrainAsync(processor);

        await processor.ResetCheckpointAsync(projection.Name);
        Assert.False(await processor.RetryFailureAsync(projection.Name, 1));
    }
}
