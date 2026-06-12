// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Mcp;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Mcp;

/// <summary>
/// Integration tests for the MCP tool surface (phase 13). The tools are tested
/// directly — they are plain methods; the MCP transport adds nothing to verify here.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpToolsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public McpToolsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record McpDoc(string Id, string Name, [property: SensitiveData] string? Secret = null);

    private sealed record McpEvent(string UserId);

    private sealed class NoopChangeHandler : IChangeHandler
    {
        public string Name => "mcp-test-projection";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopEventHandler : IEventHandler
    {
        public string Name => "mcp-test-event-projection";

        public Task HandleAsync(EventRecord @event, CancellationToken ct) => Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<McpDoc>()
            .Event<McpEvent>()
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private PapumaKernelTools CreateTools(
        ChangeFeedProcessor change, EventFeedProcessor events, PapumaMcpOptions? options = null) =>
        new(_store, change, events, options);

    [Fact]
    public async Task ModelInventory_DescribesTypesAndPolicies()
    {
        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        var tools = CreateTools(change, events);

        var inventory = (JsonObject)JsonNode.Parse(tools.GetModelInventory())!;
        var doc = Assert.Single(inventory["documents"]!.AsArray(),
            d => (string?)d!["documentType"] == nameof(McpDoc));
        Assert.Contains(doc!["fields"]!.AsArray(),
            f => (string?)f!["path"] == "secret" && (string?)f["policy"] == "Redact");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task DocumentHistory_IsPolicyApplied_AndScopeBound()
    {
        var tenant = $"t{Guid.NewGuid():N}";
        var id = NewId();

        await using (var session = _store.OpenSession(ScopeContext.Tenant(tenant)))
        {
            await session.SaveAsync(new McpDoc(id, "Harry", Secret: "s3cret"), 0);
            var loaded = await session.LoadAsync<McpDoc>(id);
            await session.SaveAsync(loaded!.Document with { Name = "Harald" }, 1);
            await session.CommitAsync();
        }

        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        var tools = CreateTools(change, events);

        var history = (JsonArray)JsonNode.Parse(
            await tools.GetDocumentHistoryAsync(nameof(McpDoc), id, tenant))!;
        Assert.Equal(2, history.Count);
        Assert.Equal("Insert", (string?)history[0]!["operation"]);

        var secretEntry = history[0]!["diff"]!["secret"]!.AsObject();
        Assert.True((bool)secretEntry["changed"]!);
        Assert.False(secretEntry.ContainsKey("new")); // policy applied — no value leaks

        // Other tenant sees nothing (scope-bound).
        var foreign = (JsonArray)JsonNode.Parse(
            await tools.GetDocumentHistoryAsync(nameof(McpDoc), id, $"t{Guid.NewGuid():N}"))!;
        Assert.Empty(foreign);

        await Assert.ThrowsAsync<ArgumentException>(
            () => tools.GetDocumentHistoryAsync("Nope", id, tenant));
    }

    [Fact]
    public async Task FeedLagAndFailures_ReportBothFeeds()
    {
        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        await change.ProcessOnceAsync();
        await events.ProcessOnceAsync();
        var tools = CreateTools(change, events);

        var lag = (JsonObject)JsonNode.Parse(await tools.GetFeedLagAsync())!;
        Assert.Contains(lag["changeFeed"]!.AsArray(),
            l => (string?)l!["handler"] == "mcp-test-projection");
        Assert.Contains(lag["eventFeed"]!.AsArray(),
            l => (string?)l!["handler"] == "mcp-test-event-projection");

        var failures = (JsonObject)JsonNode.Parse(await tools.GetFeedFailuresAsync())!;
        Assert.NotNull(failures["changeFeed"]);
        Assert.NotNull(failures["eventFeed"]);
    }

    [Fact]
    public async Task Mutations_AreDisabledByDefault_AndOptIn()
    {
        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);

        var readOnly = CreateTools(change, events);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => readOnly.RetryFeedFailureAsync("change", "mcp-test-projection", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => readOnly.ResetFeedCheckpointAsync("change", "mcp-test-projection"));

        var mutating = CreateTools(change, events, new PapumaMcpOptions { AllowMutations = true });
        Assert.Contains("No failure entry",
            await mutating.RetryFeedFailureAsync("change", "mcp-test-projection", 999));
        Assert.Contains("replays from the beginning",
            await mutating.ResetFeedCheckpointAsync("change", "mcp-test-projection"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => mutating.RetryFeedFailureAsync("neither", "x", 1));
    }
}
