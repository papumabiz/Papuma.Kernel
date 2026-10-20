// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Gdpr;

/// <summary>
/// The export carries no identifiers of other people and no free audit text unless asked
/// to (review S-3, Art. 15 (4) GDPR).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GdprExportMetadataTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record Customer(string Id, string Name);

    private sealed record Visit(string CustomerId, string Page);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Customer>().Event<Visit>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static SessionOptions Clerk() => new()
    {
        ActorId = "clerk-4711",
        CausationId = "cmd-0815",
        CausationType = "ImportCustomers",
    };

    /// <summary>A customer with one tracked change, one event and one redaction, all written by a clerk.</summary>
    private async Task<(ScopeContext Scope, string Id)> SeedAsync()
    {
        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
        var id = Guid.NewGuid().ToString("N");

        await using (var session = _store.OpenSession(scope, Clerk()))
        {
            await session.SaveAsync(new Customer(id, "Harry"), 0);
            await session.AppendAsync(new Visit(id, "/home"));
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(scope, Clerk()))
        {
            await session.RedactHistoryAsync<Customer>(id, reason: "erasure request of Mrs. Example, ticket 4711", paths: ["name"]);
            await session.CommitAsync();
        }

        return (scope, id);
    }

    private static IEnumerable<JsonObject> MetadataOf(JsonObject export)
    {
        var document = Assert.Single(export["documents"]!.AsArray())!;
        foreach (var change in document["history"]!.AsArray())
        {
            yield return change!["metadata"]!.AsObject();
        }

        foreach (var evt in export["events"]!.AsArray())
        {
            yield return evt!["metadata"]!.AsObject();
        }
    }

    [Fact]
    public async Task Export_ByDefault_LeavesOutActorsCausationTraceAndFreeAuditText()
    {
        var (scope, id) = await SeedAsync();

        var export = await GdprExport.ExportAsync(_store, scope,
            [new DocumentRef(nameof(Customer), id)],
            [new EventSelector(nameof(Visit), "customerId", id)]);

        var metadata = MetadataOf(export).ToList();
        Assert.Equal(2, metadata.Count);
        var serialized = export.ToJsonString();
        Assert.DoesNotContain("clerk-4711", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd-0815", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportCustomers", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Mrs. Example", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("traceparent", serialized, StringComparison.Ordinal);

        // What the data subject is entitled to see stays: that a change belonged to a unit of work,
        // and that and when parts of the history were erased.
        Assert.All(metadata, m => Assert.NotNull(m["correlationId"]));
        var redaction = metadata[0]["redaction"]!.AsObject();
        Assert.NotNull(redaction["redactedAt"]);
        Assert.Equal("name", (string?)redaction["paths"]!.AsArray()[0]);
    }

    [Fact]
    public async Task Export_WithIncludeActorMetadata_DeliversTheFullMetadata()
    {
        var (scope, id) = await SeedAsync();

        var export = await GdprExport.ExportAsync(_store, scope,
            [new DocumentRef(nameof(Customer), id)],
            [new EventSelector(nameof(Visit), "customerId", id)],
            new GdprExportOptions { IncludeActorMetadata = true });

        var metadata = MetadataOf(export).ToList();
        Assert.All(metadata, m =>
        {
            Assert.Equal("clerk-4711", (string?)m["actorId"]);
            Assert.Equal("cmd-0815", (string?)m["causationId"]);
            Assert.Equal("ImportCustomers", (string?)m["causationType"]);
        });
        var redaction = metadata[0]["redaction"]!.AsObject();
        Assert.Equal("erasure request of Mrs. Example, ticket 4711", (string?)redaction["reason"]);
        Assert.Equal("clerk-4711", (string?)redaction["actorId"]);
    }

    [Fact]
    public async Task Export_RejectsNullOptions()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => GdprExport.ExportAsync(
            _store, ScopeContext.Tenant("t-null-options"), [], events: null, options: null!));

        Assert.Equal("options", ex.ParamName);
    }

    private static JsonObject FullMetadata() => (JsonObject)JsonNode.Parse("""
        {
          "correlationId": "c1", "actorId": "clerk", "causationId": "cmd", "causationType": "Import",
          "traceparent": "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
          "isRollback": true, "restoredVersion": 2, "somethingNewer": "x",
          "redaction": {
            "redactedAt": "2026-01-01T00:00:00Z", "reason": "ticket 1", "correlationId": "c2",
            "actorId": "dpo", "paths": ["name"], "somethingNewer": "y"
          }
        }
        """)!;

    [Fact]
    public void FilterMetadata_KeepsOnlyTheAllowList()
    {
        var filtered = GdprExport.FilterMetadata(FullMetadata(), new GdprExportOptions());

        Assert.Equal(
            JsonNode.Parse("""
                {
                  "correlationId": "c1", "isRollback": true, "restoredVersion": 2,
                  "redaction": { "redactedAt": "2026-01-01T00:00:00Z", "correlationId": "c2", "paths": ["name"] }
                }
                """)!.ToJsonString(),
            filtered.ToJsonString());
    }

    [Fact]
    public void FilterMetadata_WithIncludeActorMetadata_ReturnsEverything()
    {
        var full = FullMetadata();

        var filtered = GdprExport.FilterMetadata(full, new GdprExportOptions { IncludeActorMetadata = true });

        Assert.Equal(full.ToJsonString(), filtered.ToJsonString());
    }

    [Fact]
    public void FilterMetadata_OfEmptyMetadata_IsEmpty()
    {
        Assert.Empty(GdprExport.FilterMetadata(new JsonObject(), new GdprExportOptions()));
    }
}
