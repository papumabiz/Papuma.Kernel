// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using ModelContextProtocol;

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

    private sealed record McpHiddenDoc(string Id, string Name);

    private sealed record McpEvent(string UserId);

    private sealed class NoopChangeHandler : IChangeHandler, IProjection
    {
        public string Name => "mcp-test-projection";

        public int Version => 1;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopEffectHandler : IChangeHandler
    {
        public string Name => "mcp-test-effect";

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
            .Document<McpDoc>(d => d.ExposeToMcp())
            .Document<McpHiddenDoc>() // not exposed
            .Event<McpEvent>()
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    // These tests are about the tools' content, not the binding (McpScopeBindingTests): the agent's tenantId is
    // trusted here, as before 3.0.
    private PapumaKernelTools CreateTools(
        ChangeFeedProcessor change, EventFeedProcessor events, PapumaMcpOptions? options = null) =>
        new(_store, change, events, (options ?? new PapumaMcpOptions()) with { ScopeResolver = McpScopeResolvers.TrustToolArgument });

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

        var page = (JsonObject)JsonNode.Parse(
            await tools.GetDocumentHistoryAsync(nameof(McpDoc), id, tenant))!;
        var history = page["records"]!.AsArray();
        Assert.False((bool)page["truncated"]!);
        Assert.Equal(2, history.Count);
        Assert.Equal("Insert", (string?)history[0]!["operation"]);

        var secretEntry = history[0]!["diff"]!["secret"]!.AsObject();
        Assert.True((bool)secretEntry["changed"]!);
        Assert.False(secretEntry.ContainsKey("new")); // policy applied — no value leaks

        // Other tenant sees nothing (scope-bound).
        var foreign = JsonNode.Parse(
            await tools.GetDocumentHistoryAsync(nameof(McpDoc), id, $"t{Guid.NewGuid():N}"))!["records"]!.AsArray();
        Assert.Empty(foreign);

        var unknown = await Assert.ThrowsAsync<McpException>(
            () => tools.GetDocumentHistoryAsync("Nope", id, tenant));
        Assert.Contains("get_model_inventory", unknown.Message);
    }

    [Fact]
    public async Task ChangesByCorrelation_ReturnTheUnitOfWork_PolicyApplied_AndScopeBound()
    {
        var tenant = $"t{Guid.NewGuid():N}";
        var first = NewId();
        var second = NewId();
        Guid correlationId;
        await using (var session = _store.OpenSession(ScopeContext.Tenant(tenant)))
        {
            correlationId = session.CorrelationId;
            await session.SaveAsync(new McpDoc(first, "Harry", Secret: "s3cret"), 0);
            await session.SaveAsync(new McpDoc(second, "Sally"), 0);
            await session.CommitAsync();
        }

        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        var tools = CreateTools(change, events);

        var changes = JsonNode.Parse(
            await tools.GetChangesByCorrelationAsync(correlationId.ToString(), tenant))!["records"]!.AsArray();
        Assert.Equal([first, second], changes.Select(c => (string?)c!["documentId"]));
        Assert.False(changes[0]!["diff"]!["secret"]!.AsObject().ContainsKey("new")); // policy applied

        var foreign = JsonNode.Parse(
            await tools.GetChangesByCorrelationAsync(correlationId.ToString(), $"t{Guid.NewGuid():N}"))!["records"]!.AsArray();
        Assert.Empty(foreign);

        await Assert.ThrowsAsync<McpException>(
            () => tools.GetChangesByCorrelationAsync("not-a-guid", tenant));
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
        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler(), new NoopEffectHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);

        var readOnly = CreateTools(change, events);
        await Assert.ThrowsAsync<McpException>(
            () => readOnly.RetryFeedFailureAsync("change", "mcp-test-projection", 1));
        await Assert.ThrowsAsync<McpException>(
            () => readOnly.ResetFeedCheckpointAsync("change", "mcp-test-projection"));

        var mutating = CreateTools(change, events, new PapumaMcpOptions { AllowMutations = true });
        Assert.Contains("No failure entry",
            await mutating.RetryFeedFailureAsync("change", "mcp-test-projection", 999));
        Assert.Contains("replays from the beginning",
            await mutating.ResetFeedCheckpointAsync("change", "mcp-test-projection"));
        var refused = await Assert.ThrowsAsync<McpException>(
            () => mutating.ResetFeedCheckpointAsync("change", "mcp-test-effect"));
        Assert.Contains("not a projection", refused.Message);

        var lag = JsonNode.Parse(await mutating.GetFeedLagAsync())!["changeFeed"]!.AsArray();
        Assert.Equal("projection", (string?)lag.Single(h => (string?)h!["handler"] == "mcp-test-projection")!["kind"]);
        Assert.Equal("effect", (string?)lag.Single(h => (string?)h!["handler"] == "mcp-test-effect")!["kind"]);

        var badFeed = await Assert.ThrowsAsync<McpException>(
            () => mutating.RetryFeedFailureAsync("neither", "x", 1));
        Assert.Contains("'change' or 'event'", badFeed.Message);
        var unknownHandler = await Assert.ThrowsAsync<McpException>(
            () => mutating.ResetFeedCheckpointAsync("change", "no-such-handler"));
        Assert.NotEmpty(unknownHandler.Message);
    }

    [Fact]
    public async Task InvalidTenantId_IsRejectedWithActionableMessage_OnEveryScopedTool()
    {
        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        var tools = CreateTools(change, events);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.GetDocumentHistoryAsync(nameof(McpDoc), "x", "bad tenant!"));
        Assert.Contains("tenantId is not valid", ex.Message);
        Assert.Contains("Omit it", ex.Message);

        await Assert.ThrowsAsync<McpException>(
            () => tools.GetChangesByCorrelationAsync(Guid.NewGuid().ToString(), ""));
        await Assert.ThrowsAsync<McpException>(
            () => tools.GetDocumentAsync(nameof(McpDoc), "x", "a"));
        await Assert.ThrowsAsync<McpException>(
            () => tools.GetDocumentByKeyAsync(nameof(McpDoc), "name", "v", "a b"));
    }

    [Fact]
    public async Task GetDocument_ForUnknownOrNotExposedType_TellsTheAgent()
    {
        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        var tools = CreateTools(change, events);
        var tenant = $"t{Guid.NewGuid():N}";

        var unknown = await Assert.ThrowsAsync<McpException>(
            () => tools.GetDocumentAsync("Nope", "x", tenant));
        Assert.Contains("not registered", unknown.Message);

        var notExposed = await Assert.ThrowsAsync<McpException>(
            () => tools.GetDocumentAsync(nameof(McpHiddenDoc), "x", tenant));
        Assert.Contains("not exposed", notExposed.Message);
    }

    [Fact]
    public async Task FeedFailures_TruncateLongErrors_AndCapTheEntryCount()
    {
        var handler = $"mcp-trunc-{Guid.NewGuid():N}";
        await using (var conn = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO papuma.failure (handler_name, seq, attempts, last_error, next_retry_at)
                SELECT @handler, g, 1, repeat('x', 5000), now() FROM generate_series(1, 5) g
                """;
            cmd.Parameters.AddWithValue("handler", handler);
            await cmd.ExecuteNonQueryAsync();
        }

        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NamedChangeHandler(handler)]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);

        var all = (JsonObject)JsonNode.Parse(
            await CreateTools(change, events).GetFeedFailuresAsync())!;
        var entries = all["changeFeed"]!.AsArray();
        Assert.Equal(5, entries.Count);
        Assert.False((bool)all["truncated"]!);
        foreach (var entry in entries)
        {
            var lastError = (string)entry!["lastError"]!;
            Assert.True(lastError.Length <= 301);
            Assert.EndsWith("…", lastError);
        }

        var capped = (JsonObject)JsonNode.Parse(await CreateTools(
            change, events, new PapumaMcpOptions { MaxRecordsPerCall = 3 }).GetFeedFailuresAsync())!;
        Assert.Equal(3, capped["changeFeed"]!.AsArray().Count);
        Assert.True((bool)capped["truncated"]!);
    }

    [Fact]
    public async Task History_BeyondMaxRecordsPerCall_IsCutOffAndMarkedTruncated()
    {
        var tenant = $"t{Guid.NewGuid():N}";
        var id = NewId();
        await using (var session = _store.OpenSession(ScopeContext.Tenant(tenant)))
        {
            await session.SaveAsync(new McpDoc(id, "v1"), 0);
            await session.CommitAsync();
            for (var version = 1; version < 5; version++)
            {
                var loaded = await session.LoadAsync<McpDoc>(id);
                await session.SaveAsync(loaded!.Document with { Name = $"v{version + 1}" }, version);
                await session.CommitAsync();
            }
        }

        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);

        var exactly = JsonNode.Parse(await CreateTools(change, events, new PapumaMcpOptions { MaxRecordsPerCall = 5 })
            .GetDocumentHistoryAsync(nameof(McpDoc), id, tenant))!;
        Assert.Equal(5, exactly["records"]!.AsArray().Count);
        Assert.False((bool)exactly["truncated"]!); // exactly the cap is not "more"

        var tools = CreateTools(change, events, new PapumaMcpOptions { MaxRecordsPerCall = 3 });
        var cut = JsonNode.Parse(await tools.GetDocumentHistoryAsync(nameof(McpDoc), id, tenant))!;
        Assert.Equal(3, cut["records"]!.AsArray().Count);
        Assert.True((bool)cut["truncated"]!);
        Assert.Equal(1, (long)cut["records"]![0]!["version"]!); // oldest first
    }

    [Fact]
    public async Task Correlation_BeyondMaxRecordsPerCall_IsCutOffAndMarkedTruncated()
    {
        var tenant = $"t{Guid.NewGuid():N}";
        Guid correlationId;
        await using (var session = _store.OpenSession(ScopeContext.Tenant(tenant)))
        {
            correlationId = session.CorrelationId;
            for (var i = 0; i < 4; i++)
            {
                await session.SaveAsync(new McpDoc(NewId(), $"d{i}"), 0);
            }

            await session.CommitAsync();
        }

        using var change = new ChangeFeedProcessor(_fixture.DataSource, [new NoopChangeHandler()]);
        using var events = new EventFeedProcessor(_fixture.DataSource, [new NoopEventHandler()]);
        var tools = CreateTools(change, events, new PapumaMcpOptions { MaxRecordsPerCall = 3 });

        var cut = JsonNode.Parse(await tools.GetChangesByCorrelationAsync(correlationId.ToString(), tenant))!;
        Assert.Equal(3, cut["records"]!.AsArray().Count);
        Assert.True((bool)cut["truncated"]!);
    }

    [Fact]
    public void MaxRecordsPerCall_MustBePositive_AndDefaultsTo500()
    {
        Assert.Equal(500, new PapumaMcpOptions().MaxRecordsPerCall);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PapumaMcpOptions { MaxRecordsPerCall = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PapumaMcpOptions { MaxRecordsPerCall = -1 });
    }

    private sealed class NamedChangeHandler(string name) : IChangeHandler
    {
        public string Name => name;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }
}
