// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// The SQLite counterpart of <c>GdprToolingTests.RedactHistory_OfANestedPath_ErasesItInsideWholeObjectEntries</c>:
/// erasing a nested path reaches the entries that carry the whole object (insert, cleared object), not only the
/// entry of the field itself.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteNestedErasureTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    private sealed record Person(string Id, string Name, Address? Address);

    private sealed record Address(string Street, string City);

    public async Task InitializeAsync() =>
        _store = await fixture.CreateStoreAsync(new KernelModelBuilder().Document<Person>().Build());

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ErasingANestedPath_ReachesTheWholeObjectEntries()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = Guid.NewGuid().ToString("N");
        var person = new Person(id, "Harry", new Address("Main St 1", "Bonn"));
        await session.SaveAsync(person, 0);
        await session.SaveAsync(person with { Address = new Address("Main St 2", "Bonn") }, 1);
        await session.SaveAsync(person with { Address = null }, 2);

        Assert.Equal(3, await session.RedactHistoryAsync<Person>(id, "erasure-4715", ["address.street"]));

        var history = await session.GetHistoryAsync<Person>(id);
        Assert.All(history, c => Assert.DoesNotContain("Main St", c.Diff.ToJson().ToJsonString(), StringComparison.Ordinal));
        Assert.Equal(DiffEntryKind.Redacted, history[0].Diff.Entries["address"].Kind);
        Assert.Equal(DiffEntryKind.Tracked, history[0].Diff.Entries["name"].Kind);
    }
}
