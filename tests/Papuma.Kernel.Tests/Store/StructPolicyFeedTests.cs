// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Review C-1 through a real PostgreSQL session: <c>[SensitiveData]</c> on a member of a <b>struct</b> was silently
/// ignored — the serializer writes a struct as a JSON object, but the model scan only descended into classes — and
/// the street went into the change feed in clear text. Counterpart of <c>NestedPolicyFeedTests</c> (a class).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StructPolicyFeedTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record Customer(string Id, string Name, Address? Address, Home Primary);

    private record struct Address([property: SensitiveData] string Street, string City);

    private record struct Home([property: SensitiveData] string Street, string City);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Customer>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AStructMembersSensitiveField_NeverReachesTheFeed_InAnyWriteOfItsContainer()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = Guid.NewGuid().ToString("N");
        var home = new Home("SECRET-HOME", "Bonn");

        var results = new List<SaveResult>
        {
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-1", "Bonn"), home), 0),        // insert
            await session.SaveAsync(new Customer(id, "Harry", null, home), 1),                                    // struct -> null
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-2", "Köln"), home), 2),         // null -> struct
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-3", "Köln"), new Home("SECRET-HOME-2", "Bonn")), 3),
        };
        results.Add(await session.DeleteAsync<Customer>(id, 4));                                                  // delete

        var history = await session.GetHistoryAsync<Customer>(id);

        Assert.Equal(5, history.Count);
        foreach (var diff in results.Select(r => r.Diff).Concat(history.Select(h => h.Diff)))
        {
            Assert.DoesNotContain("SECRET", diff.ToJson().ToJsonString(), StringComparison.Ordinal);

            // Wherever the street has an entry of its own, it is a redaction marker, never the value.
            foreach (var path in new[] { "address.street", "primary.street" })
            {
                if (diff.Entries.TryGetValue(path, out var entry))
                {
                    Assert.Equal(DiffEntryKind.Redacted, entry.Kind);
                }
            }
        }

        // The insert, the field change and the delete each touch both structs.
        foreach (var index in new[] { 0, 3, 4 })
        {
            Assert.Equal(DiffEntryKind.Redacted, history[index].Diff.Entries["address.street"].Kind);
            Assert.Equal(DiffEntryKind.Redacted, history[index].Diff.Entries["primary.street"].Kind);
        }

        Assert.Equal("Bonn", (string?)history[0].Diff.Entries["address"].New!["city"]); // the rest stays tracked
        Assert.Equal("Bonn", (string?)history[0].Diff.Entries["primary"].New!["city"]);
    }
}
