// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Papuma.Kernel.AspNetCore.Processing;
using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Mcp;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// Reading must never rebuild, and a rebuild must survive a crash (review P-4, P-13, P-14):
/// <c>GetLagAsync</c> — behind the health check, the dashboard and the MCP tool
/// <c>get_feed_lag</c> — is a pure read; a manual reset leaves a marker before it empties the
/// target; checkpoint tools accept only the handlers of their own feed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectionSafetyTests : IAsyncLifetime
{
    private static readonly ChangeFeedProcessorOptions WholeBacklog = new() { BatchSize = 100_000 };

    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public ProjectionSafetyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record SafetyDoc(string Id, string Name);

    public async Task InitializeAsync() =>
        _store = await _fixture.Database.CreateStoreAsync(new KernelModelBuilder().Document<SafetyDoc>().Build());

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewName() => $"safety-{Guid.NewGuid():N}";

    /// <summary>
    /// A projection whose target is an in-memory list. <see cref="FailResetOnce"/> makes the next
    /// <c>ResetAsync</c> empty the target and then throw — what a crash in the middle of a rebuild leaves.
    /// </summary>
    private sealed class DocProjection(string name, int version) : IChangeHandler, IProjection
    {
        private int _failResets;

        public string Name => name;

        public int Version => version;

        public ConcurrentQueue<string> Target { get; } = new();

        public int Resets { get; private set; }

        public void FailResetOnce() => _failResets = 1;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType == nameof(SafetyDoc))
            {
                Target.Enqueue(change.DocumentId);
            }

            return Task.CompletedTask;
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

    private ChangeFeedProcessor Processor(params IChangeHandler[] handlers) =>
        new(_fixture.Database.AppDataSource, handlers, WholeBacklog);

    private async Task<int?> StoredVersionAsync(string key)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT projection_version FROM papuma.checkpoint WHERE handler_name = @name");
        cmd.Parameters.AddWithValue("name", key);
        return await cmd.ExecuteScalarAsync() as int?;
    }

    // ── P-4: reading is not rebuilding ─────────────────────────────────────────

    [Fact]
    public async Task GetLag_OnANodeThatDeclaresANewerVersion_DoesNotRebuildTheProjection()
    {
        var name = NewName();
        var ids = await WriteAsync(2);
        var old = new DocProjection(name, version: 1);
        using (var worker = Processor(old))
        {
            await worker.DrainAsync();
        }

        Assert.Superset(ids.ToHashSet(), old.Target.ToHashSet());

        // A rolling deploy: the first load-balancer probe reaches a web node that declares v2 and
        // runs no workers. Its health check only reads the lag.
        var upgraded = new DocProjection(name, version: 2);
        using var webNode = Processor(upgraded);
        await webNode.GetLagAsync();

        Assert.Equal(0, upgraded.Resets);                        // the target was not emptied
        Assert.Equal(1, await StoredVersionAsync(name));         // the stored version is untouched
        Assert.Superset(ids.ToHashSet(), old.Target.ToHashSet()); // the running worker's projection still has its rows
    }

    [Fact]
    public async Task HealthCheck_OnANodeThatDeclaresANewerVersion_DoesNotRebuildTheProjection()
    {
        var (name, upgraded) = await DrainedProjectionThenNewerNodeAsync();
        using var webNode = Processor(upgraded);
        var check = new ChangeFeedLagHealthCheck(
            webNode, Options.Create(new ChangeFeedHealthCheckOptions { MaxAllowedLag = long.MaxValue }));

        await check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("papuma", _ => null!, null, null),
        });

        Assert.Equal(0, upgraded.Resets);
        Assert.Equal(1, await StoredVersionAsync(name));
    }

    [Fact]
    public async Task McpGetFeedLag_WithoutMutations_DoesNotRebuildTheProjection()
    {
        var (name, upgraded) = await DrainedProjectionThenNewerNodeAsync();
        using var change = Processor(upgraded);
        using var events = new EventFeedProcessor(_fixture.Database.AppDataSource, []);
        var tools = new PapumaKernelTools(_store, change, events); // read-only: AllowMutations is off

        await tools.GetFeedLagAsync(); // documented as "Mutating? no"

        Assert.Equal(0, upgraded.Resets);
        Assert.Equal(1, await StoredVersionAsync(name));
    }

    // A worker drained a projection at v1; a node that declares v2 (a rolling deploy) now appears.
    private async Task<(string Name, DocProjection Upgraded)> DrainedProjectionThenNewerNodeAsync()
    {
        var name = NewName();
        await WriteAsync(2);
        using (var worker = Processor(new DocProjection(name, version: 1)))
        {
            await worker.DrainAsync();
        }

        Assert.Equal(1, await StoredVersionAsync(name));
        return (name, new DocProjection(name, version: 2));
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
        Assert.Null(await StoredVersionAsync(name)); // no checkpoint row was created by looking
    }

    // ── P-14: a rebuild that is cut short is finished, not forgotten ────────────

    [Fact]
    public async Task ManualReset_InterruptedAfterEmptyingTheTarget_IsRepeatedOnTheNextStart()
    {
        var name = NewName();
        var ids = await WriteAsync(3);
        var projection = new DocProjection(name, version: 1);
        using (var worker = Processor(projection))
        {
            await worker.DrainAsync();
            Assert.Superset(ids.ToHashSet(), projection.Target.ToHashSet());

            projection.FailResetOnce();
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ResetProjectionAsync(name));
            Assert.Empty(projection.Target); // the target is gone, the checkpoint still says "delivered"
        }

        // The process "restarts": a new processor. Before the fix nothing recorded that the target had
        // been emptied, so the projection stayed empty for good while its checkpoint sat at the head.
        using (var restarted = Processor(projection))
        {
            await restarted.DrainAsync();
        }

        Assert.Superset(ids.ToHashSet(), projection.Target.ToHashSet());
        Assert.Equal(1, await StoredVersionAsync(name)); // and the marker is gone
    }

    [Fact]
    public async Task VersionRebuild_WhoseResetFailsOnce_IsRetriedAndCompletesOnce()
    {
        var name = NewName();
        var ids = await WriteAsync(2);
        using (var worker = Processor(new DocProjection(name, version: 1)))
        {
            await worker.DrainAsync();
        }

        var upgraded = new DocProjection(name, version: 2);
        upgraded.FailResetOnce();
        using var processor = Processor(upgraded);

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessOnceAsync());
        Assert.Equal(1, await StoredVersionAsync(name)); // not recorded as rebuilt

        await processor.DrainAsync();

        Assert.Equal(2, upgraded.Resets); // the failed attempt and the one that counted
        Assert.Equal(2, await StoredVersionAsync(name));
        Assert.Superset(ids.ToHashSet(), upgraded.Target.ToHashSet());
    }

    // ── P-13: checkpoint tools know their own feed ──────────────────────────────

    [Theory]
    [InlineData("event:orders")]
    [InlineData("a-typo")]
    public async Task ChangeFeed_ResetAndRetry_RejectAHandlerThatIsNotInThisFeed(string handlerName)
    {
        using var processor = Processor(new DocProjection(NewName(), version: 1));

        var reset = await Assert.ThrowsAsync<ArgumentException>(() => processor.ResetCheckpointAsync(handlerName));
        Assert.Equal("handlerName", reset.ParamName);
        var retry = await Assert.ThrowsAsync<ArgumentException>(() => processor.RetryFailureAsync(handlerName, 1));
        Assert.Equal("handlerName", retry.ParamName);
        Assert.Null(await StoredVersionAsync(handlerName)); // and no orphan checkpoint row appeared
    }

    [Fact]
    public async Task EventFeed_ResetAndRetry_RejectAHandlerThatIsNotInThisFeed()
    {
        using var processor = new EventFeedProcessor(
            _fixture.Database.AppDataSource, [new NoopEventHandler(NewName())], WholeBacklog);

        var reset = await Assert.ThrowsAsync<ArgumentException>(() => processor.ResetCheckpointAsync("a-typo"));
        Assert.Equal("handlerName", reset.ParamName);
        var retry = await Assert.ThrowsAsync<ArgumentException>(() => processor.RetryFailureAsync("a-typo", 1));
        Assert.Equal("handlerName", retry.ParamName);
    }

    [Fact]
    public async Task ChangeFeed_ResetAndRetry_StillWorkForItsOwnHandler()
    {
        var projection = new DocProjection(NewName(), version: 1);
        using var processor = Processor(projection);
        await WriteAsync(1);
        await processor.DrainAsync();

        await processor.ResetCheckpointAsync(projection.Name);
        Assert.False(await processor.RetryFailureAsync(projection.Name, 1)); // no such failure, but no error either
    }
}
