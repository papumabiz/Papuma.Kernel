// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Session-level proof of the patch semantics pinned by <c>docs/spec/vectors/patch.json</c>
/// — the SQLite counterpart of <c>PatchOrderingTests</c> in the PostgreSQL suite.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqlitePatchOrderingTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    private sealed record Counter(string Id, string Name, int Value = 0);

    private sealed record Address(string City);

    private sealed record Profile(string Id, Address? Address = null);

    public async Task InitializeAsync() =>
        _store = await fixture.CreateStoreAsync(new KernelModelBuilder().Document<Counter>().Document<Profile>().Build());

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task Patch_TwoIncrementsOnTheSameField_Accumulate()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "hits", Value: 1), 0);

        await session.PatchAsync<Counter>(id, p => p.Increment(x => x.Value, 2).Increment(x => x.Value, 3));

        Assert.Equal(6, (await session.LoadAsync<Counter>(id))!.Document.Value);
    }

    [Fact]
    public async Task Patch_IncrementAfterSet_BuildsOnTheSetValue()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "hits", Value: 1), 0);

        await session.PatchAsync<Counter>(id, p => p.Set(x => x.Value, 10).Increment(x => x.Value, 5));

        Assert.Equal(15, (await session.LoadAsync<Counter>(id))!.Document.Value);
    }

    [Fact]
    public async Task Patch_IncrementOnNonNumericField_ThrowsTyped_AndLeavesTheDocumentUntouched()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "hits"), 0);

        var ex = await Assert.ThrowsAsync<NonNumericIncrementException>(
            () => session.PatchAsync<Counter>(id, p => p.Increment(x => x.Name)));

        Assert.Equal(nameof(Counter), ex.DocumentType);
        Assert.Equal(id, ex.DocumentId);
        Assert.Equal("name", ex.FieldPath);
        var loaded = await session.LoadAsync<Counter>(id);
        Assert.Equal("hits", loaded!.Document.Name);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task PatchMany_IncrementOnNonNumericField_ThrowsTyped_AndRollsBack()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "hits"), 0);

        await Assert.ThrowsAsync<NonNumericIncrementException>(
            () => session.PatchManyAsync<Counter>([id], p => p.Increment(x => x.Name)));

        Assert.Equal(1, (await session.LoadAsync<Counter>(id))!.Version);
    }

    [Fact]
    public async Task Patch_SetBelowAMissingParent_ThrowsTyped_AndLeavesTheDocumentUntouched()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Profile(id), 0);

        var ex = await Assert.ThrowsAsync<PatchTargetMissingException>(
            () => session.PatchAsync<Profile>(id, p => p.Set(x => x.Address!.City, "Bonn")));

        Assert.Equal(nameof(Profile), ex.DocumentType);
        Assert.Equal(1, (await session.LoadAsync<Profile>(id))!.Version);
    }

    [Fact]
    public async Task Patch_SetTheParentThenTheChild_Succeeds()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Profile(id), 0);

        await session.PatchAsync<Profile>(id, p => p
            .Set(x => x.Address, new Address("Köln"))
            .Set(x => x.Address!.City, "Bonn"));

        Assert.Equal("Bonn", (await session.LoadAsync<Profile>(id))!.Document.Address!.City);
    }

    [Fact]
    public async Task PatchMany_SetBelowAMissingParent_ThrowsTyped_AndRollsBackEveryDocument()
    {
        await using var session = _store.OpenSession(NewTenant());
        var withParent = NewId();
        var withoutParent = NewId();
        await session.SaveAsync(new Profile(withParent, new Address("Köln")), 0);
        await session.SaveAsync(new Profile(withoutParent), 0);

        await Assert.ThrowsAsync<PatchTargetMissingException>(
            () => session.PatchManyAsync<Profile>([withParent, withoutParent], p => p.Set(x => x.Address!.City, "Bonn")));

        var loaded = await session.LoadAsync<Profile>(withParent);
        Assert.Equal("Köln", loaded!.Document.Address!.City);
        Assert.Equal(1, loaded.Version);
    }
}
