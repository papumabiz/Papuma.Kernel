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
/// Declared projections and effects (ADR-024): versioned rebuild, the first start with a
/// stored checkpoint, rolling deploys, reset, and effects that start at the head.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectionLifecycleTests : IAsyncLifetime
{
    private static readonly ChangeFeedProcessorOptions WholeBacklog = new() { BatchSize = 100_000 };

    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public ProjectionLifecycleTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record LifecycleDoc(string Id, string Name);

    private sealed record LifecycleEvent(string Name);

    public async Task InitializeAsync()
    {
        _store = await _fixture.Database.CreateStoreAsync(
            new KernelModelBuilder().Document<LifecycleDoc>().Event<LifecycleEvent>().Build());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewName() => $"lifecycle-{Guid.NewGuid():N}";

    /// <summary>A projection over this test's documents; the "target" is its in-memory list.</summary>
    private sealed class DocProjection(string name, int version) : IChangeHandler, IProjection
    {
        public string Name => name;

        public int Version => version;

        public ConcurrentQueue<string> Target { get; } = new();

        public int Resets { get; private set; }

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType == nameof(LifecycleDoc))
            {
                Target.Enqueue(change.DocumentId);
            }

            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken ct)
        {
            Target.Clear();
            Resets++;
            return Task.CompletedTask;
        }
    }

    private sealed class DocEffect(string name) : IChangeHandler
    {
        public string Name => name;

        public ConcurrentQueue<string> Acted { get; } = new();

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType == nameof(LifecycleDoc))
            {
                Acted.Enqueue(change.DocumentId);
            }

            return Task.CompletedTask;
        }
    }

    [StartsAtFeedHead]
    private sealed class HeadEffect(string name) : IChangeHandler
    {
        public string Name => name;

        public ConcurrentQueue<string> Acted { get; } = new();

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType == nameof(LifecycleDoc))
            {
                Acted.Enqueue(change.DocumentId);
            }

            return Task.CompletedTask;
        }
    }

    [StartsAtFeedHead]
    private sealed class HeadProjection(string name) : IChangeHandler, IProjection
    {
        public string Name => name;

        public int Version => 1;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EventProjection(string name, int version) : IEventHandler, IProjection
    {
        public string Name => name;

        public int Version => version;

        public ConcurrentQueue<long> Target { get; } = new();

        public int Resets { get; private set; }

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            if (@event.EventType == nameof(LifecycleEvent))
            {
                Target.Enqueue(@event.Seq);
            }

            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken ct)
        {
            Target.Clear();
            Resets++;
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
            await session.SaveAsync(new LifecycleDoc(id, "doc"), 0);
            await session.AppendAsync(new LifecycleEvent("event"));
            ids.Add(id);
        }

        await session.CommitAsync();
        return ids;
    }

    private async Task DrainAsync(IChangeHandler handler)
    {
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [handler], WholeBacklog);
        await processor.DrainAsync();
    }

    private async Task<int?> StoredVersionAsync(string key)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT projection_version FROM papuma.checkpoint WHERE handler_name = @name");
        cmd.Parameters.AddWithValue("name", key);
        return await cmd.ExecuteScalarAsync() as int?;
    }

    [Fact]
    public async Task NewProjection_Backfills_AndRecordsItsVersion()
    {
        var before = await WriteAsync(2);
        var projection = new DocProjection(NewName(), version: 3);

        await DrainAsync(projection);

        Assert.Superset(before.ToHashSet(), projection.Target.ToHashSet());
        Assert.Equal(3, await StoredVersionAsync(projection.Name));
        Assert.Equal(0, projection.Resets);
    }

    [Fact]
    public async Task VersionBump_ResetsTheTarget_AndReplaysOnce()
    {
        var name = NewName();
        var ids = await WriteAsync(2);
        await DrainAsync(new DocProjection(name, version: 1));

        var bumped = new DocProjection(name, version: 2);
        bumped.Target.Enqueue("stale row the reset must remove");
        await DrainAsync(bumped);

        Assert.Equal(1, bumped.Resets);
        Assert.DoesNotContain("stale row the reset must remove", bumped.Target);
        Assert.Superset(ids.ToHashSet(), bumped.Target.ToHashSet());
        Assert.Equal(2, await StoredVersionAsync(name));

        var again = new DocProjection(name, version: 2); // a second instance of the same deploy
        await DrainAsync(again);
        Assert.Equal(0, again.Resets);
        Assert.Empty(again.Target); // nothing replayed
    }

    [Fact]
    public async Task FirstStartWithAStoredCheckpoint_RecordsTheVersion_WithoutRebuild()
    {
        var name = NewName();
        await WriteAsync(1);
        await DrainAsync(new DocEffect(name)); // a checkpoint from before ADR-024: no version
        Assert.Null(await StoredVersionAsync(name));

        var projection = new DocProjection(name, version: 5);
        await DrainAsync(projection);

        Assert.Equal(0, projection.Resets);
        Assert.Empty(projection.Target); // no replay
        Assert.Equal(5, await StoredVersionAsync(name));
    }

    [Fact]
    public async Task OlderInstance_PausesTheProjection_EvenWhenTheRebuildHappensWhileItRuns()
    {
        var name = NewName();
        var olderProjection = new DocProjection(name, version: 1);
        using var olderInstance = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [olderProjection], WholeBacklog);
        await olderInstance.DrainAsync(); // registered and caught up at version 1

        await DrainAsync(new DocProjection(name, version: 2)); // a newer deploy rebuilds

        var ids = await WriteAsync(1);
        await olderInstance.DrainAsync();
        Assert.DoesNotContain(ids[0], olderProjection.Target);

        var lag = Assert.Single(await olderInstance.GetLagAsync());
        Assert.True(lag.Paused);
        Assert.Equal(1, lag.ProjectionVersion);
        Assert.Equal(0, await olderInstance.ResetProjectionsAsync()); // never lowers the version
        Assert.Equal(2, await StoredVersionAsync(name));
    }

    [Fact]
    public async Task ResetProjectionsAsync_RebuildsProjections_AndLeavesEffectsAlone()
    {
        var ids = await WriteAsync(1);
        var projection = new DocProjection(NewName(), version: 1);
        var effect = new DocEffect(NewName());
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [projection, effect], WholeBacklog);
        await processor.DrainAsync();
        var actedBefore = effect.Acted.Count;

        Assert.Equal(1, await processor.ResetProjectionsAsync());
        await processor.DrainAsync();

        Assert.Equal(1, projection.Resets);
        Assert.Contains(ids[0], projection.Target);
        Assert.Equal(actedBefore, effect.Acted.Count); // the effect did not act again
    }

    [Fact]
    public async Task EffectStartingAtTheHead_SkipsHistory_AndActsOnNewCommits()
    {
        var history = await WriteAsync(2);
        var effect = new HeadEffect(NewName());
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [effect], WholeBacklog);
        await processor.DrainAsync();

        var fresh = await WriteAsync(1);
        await processor.DrainAsync();

        Assert.Equal(fresh, effect.Acted.ToList());
        Assert.DoesNotContain(history[0], effect.Acted);
    }

    [Fact]
    public void ProjectionStartingAtTheHead_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new ChangeFeedProcessor(_fixture.Database.AppDataSource, [new HeadProjection(NewName())]));
        Assert.Contains("cannot start at the feed head", ex.Message);
    }

    [Fact]
    public async Task EventProjection_VersionBump_ResetsAndReplays()
    {
        var name = NewName();
        await WriteAsync(2);
        using (var first = new EventFeedProcessor(_fixture.Database.AppDataSource, [new EventProjection(name, 1)], WholeBacklog))
        {
            await first.DrainAsync();
        }

        var bumped = new EventProjection(name, 2);
        using var second = new EventFeedProcessor(_fixture.Database.AppDataSource, [bumped], WholeBacklog);
        await second.DrainAsync();

        Assert.Equal(1, bumped.Resets);
        Assert.True(bumped.Target.Count >= 2);
        Assert.Equal(2, await StoredVersionAsync("event:" + name));
    }
}
