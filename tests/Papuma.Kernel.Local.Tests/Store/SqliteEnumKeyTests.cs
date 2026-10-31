// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// An enum key is stored and looked up by name (enum values are written by name since 3.0): the lookup value goes
/// through the same serializer as the document, so a key read by an enum value matches.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteEnumKeyTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    public enum Stage
    {
        Draft,
        Live,
    }

    private sealed record Page(string Id, Stage Stage);

    public async Task InitializeAsync() =>
        _store = await fixture.CreateStoreAsync(new KernelModelBuilder().Document<Page>(d => d.LookupKey(x => x.Stage)).Build());

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ALookupByEnumValue_FindsTheDocumentsStoredWithThatName()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        await session.SaveAsync(new Page("a", Stage.Live), 0);
        await session.SaveAsync(new Page("b", Stage.Draft), 0);

        var live = await session.LoadAllByKeyAsync<Page>(x => x.Stage, Stage.Live, limit: 10);

        Assert.Equal(["a"], live.Select(r => r.Document.Id));
    }
}
