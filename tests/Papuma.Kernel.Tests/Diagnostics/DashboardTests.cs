// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.AspNetCore.Dashboard;
using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Diagnostics;

/// <summary>
/// Integration tests for the embedded dashboard's data pipeline: the in-process
/// metrics collector and the JSON payload over the phase-11 diagnostics APIs.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DashboardTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public DashboardTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record DashDoc(string Id, string Name);

    private sealed class CountingHandler : IChangeHandler
    {
        public string Name { get; } = $"dash-{Guid.NewGuid():N}";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<DashDoc>().Build();
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DataPayload_CarriesCountersLagAndFailures()
    {
        using var collector = new DashboardMetricsCollector(); // listening before the writes

        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new DashDoc(Guid.NewGuid().ToString("N"), "one"), 0);
            await session.SaveAsync(new DashDoc(Guid.NewGuid().ToString("N"), "two"), 0);
            await session.CommitAsync();
        }

        var handler = new CountingHandler();
        using var change = new ChangeFeedProcessor(_fixture.DataSource, [handler]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        await change.ProcessOnceAsync();
        await events.ProcessOnceAsync();

        var json = await DashboardDataBuilder.BuildAsync(change, events, collector, CancellationToken.None);

        // Counters: the two saves are visible as cumulative totals (other tests in the
        // same process may add more — assert at-least, not exact).
        var writes = json["counters"]!.AsObject()
            .Where(p => p.Key.StartsWith("papuma.session.writes", StringComparison.Ordinal))
            .Sum(p => (long)p.Value!);
        Assert.True(writes >= 2, $"expected >= 2 writes, saw {writes}");

        // Lag: our handler appears with checkpoint == head (drained).
        var lagEntry = Assert.Single(json["changeFeed"]!["lag"]!.AsArray(),
            l => (string?)l!["handler"] == handler.Name);
        Assert.Equal(0, (long)lagEntry!["lag"]!);

        Assert.NotNull(json["eventFeed"]!["failures"]);
        Assert.NotNull(json["timestamp"]);
    }

    [Fact]
    public void DashboardPage_EmbedsTheDataPath()
    {
        var html = typeof(DashboardMetricsCollector).Assembly
            .GetType("Papuma.Kernel.AspNetCore.Dashboard.DashboardPage")!
            .GetMethod("Html", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .Invoke(null, ["/papuma/data"]) as string;

        Assert.NotNull(html);
        Assert.Contains("/papuma/data", html);
        Assert.Contains("Papuma.Kernel", html);
    }

    private sealed class NoopEventHandler : IEventHandler
    {
        public string Name { get; } = $"dash-evt-{Guid.NewGuid():N}";

        public Task HandleAsync(EventRecord @event, CancellationToken ct) => Task.CompletedTask;
    }
}
