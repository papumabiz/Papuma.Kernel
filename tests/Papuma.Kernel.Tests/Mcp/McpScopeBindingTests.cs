// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;

using Papuma.Kernel.AspNetCore.Tenancy;
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
/// The scope of the document tools comes from the operator's <see cref="PapumaMcpOptions.ScopeResolver"/>, not from
/// the agent (review M-1, decision D-3); <c>ExposeToMcp()</c> also gates history and correlation (M-2, D-4); and a
/// key lookup on a policy-protected key is refused, because it would answer "does this value exist?".
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpScopeBindingTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record BoundDoc(
        string Id,
        [property: UniqueKey] string Name,
        [property: UniqueKey, SensitiveData] string Email);

    private sealed record HiddenDoc(string Id, string Value);

    private sealed class ServiceMarker;

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<BoundDoc>(d => d.ExposeToMcp())
            .Document<HiddenDoc>() // not exposed
            .Build();

        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private PapumaKernelTools Tools(Func<McpScopeRequest, ScopeContext?>? resolver, IServiceProvider? services = null) =>
        new(
            _store,
            new ChangeFeedProcessor(fixture.DataSource, []),
            new EventFeedProcessor(fixture.DataSource, []),
            new PapumaMcpOptions { ScopeResolver = resolver },
            services);

    private async Task<(string Id, Guid CorrelationId)> SeedAsync(ScopeContext scope, bool withHidden = false)
    {
        var id = NewId();
        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(new BoundDoc(id, $"name-{id}", $"{id}@x.de"), 0);
        if (withHidden)
        {
            await session.SaveAsync(new HiddenDoc(NewId(), "internal price"), 0);
        }

        await session.CommitAsync();
        return (id, session.CorrelationId);
    }

    // ── D-3: the operator binds the scope ──────────────────────────────────────

    [Fact]
    public async Task WithoutAResolver_EveryScopeBoundToolRefuses_TheFeedAndModelToolsStillWork()
    {
        var tools = Tools(resolver: null);
        var tenant = NewTenant().TenantId;

        foreach (var call in new Func<Task>[]
                 {
                     () => tools.GetDocumentAsync(nameof(BoundDoc), "x", tenant),
                     () => tools.GetDocumentByKeyAsync(nameof(BoundDoc), "name", "v", tenant),
                     () => tools.GetDocumentHistoryAsync(nameof(BoundDoc), "x", tenant),
                     () => tools.GetChangesByCorrelationAsync(Guid.NewGuid().ToString(), tenant),
                     () => tools.GetDocumentAsync(nameof(BoundDoc), "x"),
                 })
        {
            var ex = await Assert.ThrowsAsync<McpException>(call);
            Assert.Contains("ScopeResolver", ex.Message, StringComparison.Ordinal);
        }

        Assert.NotNull(JsonNode.Parse(tools.GetModelInventory()));
        Assert.NotNull(JsonNode.Parse(await tools.GetFeedLagAsync()));
    }

    [Fact]
    public async Task ABoundTenant_IsUsedWhenTenantIdIsOmitted_AndAnotherTenantIsRefused()
    {
        var bound = NewTenant();
        var (id, correlationId) = await SeedAsync(bound);
        var tools = Tools(_ => bound);

        var own = JsonNode.Parse(await tools.GetDocumentAsync(nameof(BoundDoc), id))!;
        Assert.Equal(id, (string?)own["document"]!["id"]);
        var same = JsonNode.Parse(await tools.GetDocumentAsync(nameof(BoundDoc), id, bound.TenantId))!;
        Assert.Equal(id, (string?)same["document"]!["id"]);

        var foreign = NewTenant().TenantId;
        foreach (var call in new Func<Task>[]
                 {
                     () => tools.GetDocumentAsync(nameof(BoundDoc), id, foreign),
                     () => tools.GetDocumentByKeyAsync(nameof(BoundDoc), "name", $"name-{id}", foreign),
                     () => tools.GetDocumentHistoryAsync(nameof(BoundDoc), id, foreign),
                     () => tools.GetChangesByCorrelationAsync(correlationId.ToString(), foreign),
                 })
        {
            var ex = await Assert.ThrowsAsync<McpException>(call);
            Assert.Contains("outside the scope", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bound.TenantId!, ex.Message, StringComparison.Ordinal); // the binding is not disclosed
        }
    }

    [Fact]
    public async Task ABoundTenant_DoesNotOpenThePlatformScope()
    {
        var tools = Tools(_ => NewTenant());

        // Omitting tenantId reads in the bound tenant — it no longer means "platform".
        Assert.Equal("null", await tools.GetDocumentAsync(nameof(BoundDoc), NewId()));
    }

    [Fact]
    public async Task AResolverReturningNull_Refuses()
    {
        var tools = Tools(_ => null);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.GetDocumentAsync(nameof(BoundDoc), "x"));
        Assert.Contains("not bound to a scope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResolver_SeesTheRequestedScope_AndTheCallsServices()
    {
        var services = new ServiceCollection()
            .AddSingleton<ServiceMarker>()
            .BuildServiceProvider();
        var seen = new List<McpScopeRequest>();
        var tenant = NewTenant();
        var tools = Tools(r => { seen.Add(r); return tenant; }, services);

        await tools.GetDocumentAsync(nameof(BoundDoc), NewId(), tenant.TenantId);
        await tools.GetDocumentAsync(nameof(BoundDoc), NewId());

        Assert.Equal(tenant, seen[0].Requested);
        Assert.Null(seen[1].Requested);
        Assert.NotNull(seen[0].Services.GetService(typeof(ServiceMarker)));
    }

    [Fact]
    public async Task TrustToolArgument_RestoresTheAgentChosenScope_Deliberately()
    {
        var tenant = NewTenant();
        var (id, _) = await SeedAsync(tenant);
        var tools = Tools(McpScopeResolvers.TrustToolArgument);

        var found = JsonNode.Parse(await tools.GetDocumentAsync(nameof(BoundDoc), id, tenant.TenantId))!;
        Assert.Equal(id, (string?)found["document"]!["id"]);
        Assert.Equal("null", await tools.GetDocumentAsync(nameof(BoundDoc), id)); // omitted → platform

        Assert.Equal(ScopeContext.Platform(), McpScopeResolvers.TrustToolArgument(new McpScopeRequest(EmptyServices.Instance, null)));
    }

    [Fact]
    public async Task TheDocumentedHttpResolver_BindsTheRequestsScope_AndRefusesARequestWithoutOne()
    {
        var tenant = NewTenant();
        var (id, _) = await SeedAsync(tenant);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(accessor).BuildServiceProvider();

        // Exactly the resolver of observability.md and the XML docs.
        Func<McpScopeRequest, ScopeContext?> resolver = r => r.Services.GetRequiredService<IHttpContextAccessor>().HttpContext is { } http
            && http.TryGetScopeContext(out var scope) ? scope : null;
        var tools = Tools(resolver, services);

        var refused = await Assert.ThrowsAsync<McpException>(() => tools.GetDocumentAsync(nameof(BoundDoc), id));
        Assert.Contains("not bound to a scope", refused.Message, StringComparison.Ordinal);

        await new ScopeMiddleware(_ => Task.CompletedTask).InvokeAsync(accessor.HttpContext, new FixedResolver(tenant));
        var found = JsonNode.Parse(await tools.GetDocumentAsync(nameof(BoundDoc), id))!;
        Assert.Equal(id, (string?)found["document"]!["id"]);
    }

    private sealed class FixedResolver(ScopeContext scope) : IScopeResolver
    {
        public ScopeContext Resolve(HttpContext context) => scope;
    }

    // ── D-4: ExposeToMcp() gates history and correlation ───────────────────────

    [Fact]
    public async Task TheHistoryOfANotExposedType_IsRefused()
    {
        var tenant = NewTenant();
        var tools = Tools(_ => tenant);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.GetDocumentHistoryAsync(nameof(HiddenDoc), NewId()));
        Assert.Contains("not exposed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorrelation_ShowsRecordsOfNotExposedTypes_WithoutDiffOrMetadata()
    {
        var tenant = NewTenant();
        var (id, correlationId) = await SeedAsync(tenant, withHidden: true);
        var tools = Tools(_ => tenant);

        var records = JsonNode.Parse(await tools.GetChangesByCorrelationAsync(correlationId.ToString()))!["records"]!.AsArray();

        Assert.Equal(2, records.Count);
        var exposed = records.Single(r => (string?)r!["documentType"] == nameof(BoundDoc))!.AsObject();
        var hidden = records.Single(r => (string?)r!["documentType"] == nameof(HiddenDoc))!.AsObject();

        Assert.Equal(id, (string?)exposed["documentId"]);
        Assert.True(exposed.ContainsKey("diff"));
        Assert.True(exposed.ContainsKey("metadata"));

        Assert.False(hidden.ContainsKey("diff"));
        Assert.False(hidden.ContainsKey("metadata"));
        Assert.False((bool)hidden["exposed"]!);
        Assert.Equal("Insert", (string?)hidden["operation"]);
        Assert.Equal(1, (long)hidden["version"]!);
        Assert.DoesNotContain("internal price", records.ToJsonString(), StringComparison.Ordinal);
    }

    // ── Key lookups on protected keys are an existence oracle ──────────────────

    [Fact]
    public async Task AKeyLookup_OnAPolicyProtectedKey_IsRefused_AnUnprotectedKeyWorks()
    {
        var tenant = NewTenant();
        var (id, _) = await SeedAsync(tenant);
        var tools = Tools(_ => tenant);

        var byName = JsonNode.Parse(await tools.GetDocumentByKeyAsync(nameof(BoundDoc), "name", $"name-{id}"))!;
        Assert.Equal(id, (string?)byName["document"]!["id"]);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.GetDocumentByKeyAsync(nameof(BoundDoc), "email", $"{id}@x.de"));
        Assert.Contains("'email'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Redact", ex.Message, StringComparison.Ordinal);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
