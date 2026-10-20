// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// The SQLite counterpart of <c>StructPolicyFeedTests</c> (review C-1): <c>[SensitiveData]</c> on a member of a
/// struct is withheld from every stored diff.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteStructPolicyFeedTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    private sealed record Customer(string Id, string Name, Address? Address, Home Primary);

    private record struct Address([property: SensitiveData] string Street, string City);

    private record struct Home([property: SensitiveData] string Street, string City);

    public async Task InitializeAsync() =>
        _store = await fixture.CreateStoreAsync(new KernelModelBuilder().Document<Customer>().Build());

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AStructMembersSensitiveField_NeverReachesTheFeed_InAnyWriteOfItsContainer()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = Guid.NewGuid().ToString("N");
        var home = new Home("SECRET-HOME", "Bonn");

        var results = new List<SaveResult>
        {
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-1", "Bonn"), home), 0),
            await session.SaveAsync(new Customer(id, "Harry", null, home), 1),
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-2", "Köln"), home), 2),
            await session.SaveAsync(new Customer(id, "Harry", new Address("SECRET-3", "Köln"), new Home("SECRET-HOME-2", "Bonn")), 3),
        };
        results.Add(await session.DeleteAsync<Customer>(id, 4));

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

        Assert.Equal("Bonn", (string?)history[0].Diff.Entries["address"].New!["city"]);
        Assert.Equal("Bonn", (string?)history[0].Diff.Entries["primary"].New!["city"]);
    }
}
