// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Mcp;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Events;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Integration tests for policy-projected (masked) reads and the content MCP tools
/// (ADR-016): the masking semantics, the feed≡read hash invariant, the ExposeToMcp
/// opt-in, and declared-key-only lookups.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MaskedReadTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;
    private KernelModel _model = null!;

    public MaskedReadTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Address(string City, [property: SensitiveData] string Street);

    private sealed record MaskedUser(
        string Id,
        string Name,
        [property: UniqueKey] string Email,
        [property: SensitiveData] string? Iban = null,
        [property: TrackHash] string? PasswordHash = null,
        [property: DoNotTrack] string? LastSeenIp = null,
        Address? Address = null);

    // A type deliberately NOT exposed to MCP.
    private sealed record SecretDoc(string Id, string Value);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<MaskedUser>(d => d.ExposeToMcp())
            .Document<SecretDoc>() // not exposed
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
        _model = model;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task MaskedRead_AppliesEveryPolicy()
    {
        var scope = NewTenant();
        var id = NewId();
        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(new MaskedUser(
            id, "Harry", $"{id}@x.de",
            Iban: "DE0700",
            PasswordHash: "secret-hash-input",
            LastSeenIp: "10.0.0.1",
            Address: new Address("Cologne", "Main St 1")), 0);

        var masked = await session.LoadMaskedAsync<MaskedUser>(id);

        Assert.NotNull(masked);
        var doc = masked!.Document;
        Assert.Equal("Harry", (string?)doc["name"]);                 // Track → verbatim
        Assert.Equal($"{id}@x.de", (string?)doc["email"]);           // Track → verbatim
        Assert.Equal("[protected]", (string?)doc["iban"]);           // Redact → marker
        Assert.False(doc.ContainsKey("lastSeenIp"));                 // DoNotTrack → omitted
        Assert.Equal("Cologne", (string?)doc["address"]!["city"]);   // nested Track → verbatim
        Assert.Equal("[protected]", (string?)doc["address"]!["street"]); // nested Redact → marker

        // Hash → the SAME hash the feed would show (the ADR-016 invariant).
        var feedHash = PolicyHash.Of(JsonValue.Create("secret-hash-input"));
        Assert.Equal(feedHash, (string?)doc["passwordHash"]);
    }

    [Fact]
    public async Task MaskedRead_HashMatchesTheFeedDiffExactly()
    {
        var scope = NewTenant();
        var id = NewId();
        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(new MaskedUser(id, "H", $"{id}@x.de", PasswordHash: "pw-1"), 0);

        // The feed diff hash for the insert of passwordHash …
        var history = await session.GetHistoryAsync<MaskedUser>(id);
        var feedEntry = history[0].Diff.Entries["passwordHash"];
        Assert.Equal(DiffEntryKind.Hashed, feedEntry.Kind);

        // … must equal the masked-read hash for the same value.
        var masked = await session.LoadMaskedAsync<MaskedUser>(id);
        Assert.Equal(feedEntry.Hash, (string?)masked!.Document["passwordHash"]);
    }

    [Fact]
    public async Task MaskedRead_ReturnsNull_WhenAbsent()
    {
        await using var session = _store.OpenSession(NewTenant());
        Assert.Null(await session.LoadMaskedAsync<MaskedUser>(NewId()));
    }

    // ── MCP content tools ───────────────────────────────────────────────────────

    [Fact]
    public async Task Mcp_GetDocument_MasksAndIsScopeBound()
    {
        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
        var id = NewId();
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new MaskedUser(id, "Harry", $"{id}@x.de", Iban: "DE0700"), 0);
            await session.CommitAsync();
        }

        var tools = NewTools();
        var json = (JsonObject)JsonNode.Parse(
            await tools.GetDocumentAsync(nameof(MaskedUser), id, scope.TenantId))!;

        Assert.Equal("Harry", (string?)json["document"]!["name"]);
        Assert.Equal("[protected]", (string?)json["document"]!["iban"]);

        // Other tenant sees nothing (scope-bound).
        var foreign = await tools.GetDocumentAsync(nameof(MaskedUser), id, $"t{Guid.NewGuid():N}");
        Assert.Equal("null", foreign);
    }

    [Fact]
    public async Task Mcp_GetDocumentByKey_UsesDeclaredKey_RejectsFreeFormPaths()
    {
        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
        var id = NewId();
        var email = $"{id}@x.de";
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new MaskedUser(id, "Harry", email, Iban: "DE0700"), 0);
            await session.CommitAsync();
        }

        var tools = NewTools();

        var json = (JsonObject)JsonNode.Parse(
            await tools.GetDocumentByKeyAsync(nameof(MaskedUser), "email", email, scope.TenantId))!;
        Assert.Equal(id, (string?)json["document"]!["id"]);

        // 'name' is not a declared key → rejected (no free-form query, ADR-009).
        await Assert.ThrowsAsync<ArgumentException>(() =>
            tools.GetDocumentByKeyAsync(nameof(MaskedUser), "name", "Harry", scope.TenantId));
    }

    [Fact]
    public async Task Mcp_GetDocument_RefusesTypesNotExposed()
    {
        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
        var id = NewId();
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new SecretDoc(id, "top secret"), 0);
            await session.CommitAsync();
        }

        var tools = NewTools();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.GetDocumentAsync(nameof(SecretDoc), id, scope.TenantId));
    }

    private PapumaKernelTools NewTools()
    {
        var change = new ChangeFeedProcessor(_fixture.DataSource, []);
        var events = new EventFeedProcessor(_fixture.DataSource, []);
        return new PapumaKernelTools(_store, change, events);
    }
}
