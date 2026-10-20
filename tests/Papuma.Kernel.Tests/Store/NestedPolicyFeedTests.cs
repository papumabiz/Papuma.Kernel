// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Regression through a real PostgreSQL session: a <c>[SensitiveData]</c> field inside a nested object once
/// reached the change feed in clear whenever the whole object was one entry — on insert, when the object was
/// set or cleared, and on delete. Every stored diff must withhold it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NestedPolicyFeedTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record Customer(string Id, string Name, Address? Address);

    private sealed record Address([property: SensitiveData] string Street, string City);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Customer>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ANestedSensitiveField_NeverReachesTheFeed_InAnyWriteOfItsContainer()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = Guid.NewGuid().ToString("N");

        var results = new List<SaveResult>
        {
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-1", "Bonn")), 0), // insert
            await session.SaveAsync(new Customer(id, "Harry", null), 1),                             // object -> null
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-2", "Köln")), 2),  // null -> object
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-3", "Köln")), 3),  // field change
        };
        results.Add(await session.DeleteAsync<Customer>(id, 4));                                    // delete

        var history = await session.GetHistoryAsync<Customer>(id);

        Assert.Equal(5, history.Count);
        foreach (var diff in results.Select(r => r.Diff).Concat(history.Select(h => h.Diff)))
        {
            Assert.DoesNotContain("SECRET", diff.ToJson().ToJsonString(), StringComparison.Ordinal);
            Assert.Equal(DiffEntryKind.Redacted, diff.Entries["address.street"].Kind);
        }

        Assert.Equal("Bonn", (string?)history[0].Diff.Entries["address"].New!["city"]); // the rest stays tracked
        Assert.Equal("Köln", (string?)history[4].Diff.Entries["address"].Old!["city"]);
    }
}
