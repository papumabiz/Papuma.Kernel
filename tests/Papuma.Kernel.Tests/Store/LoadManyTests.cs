// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// <c>LoadManyAsync</c> (feedback F-17): several documents of one type by id in one query,
/// scope-bound like every read.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LoadManyTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public LoadManyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record ManyDoc(string Id, string Name);

    public async Task InitializeAsync()
    {
        _store = await _fixture.Database.CreateStoreAsync(new KernelModelBuilder().Document<ManyDoc>().Build());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task LoadsTheFoundDocuments_ByIdAndVersion_MissingOnesAreAbsent_DuplicatesOnce()
    {
        var scope = ScopeContext.Tenant(Guid.NewGuid());
        var a = NewId();
        var b = NewId();
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new ManyDoc(a, "a"), 0);
            await session.SaveAsync(new ManyDoc(b, "b"), 0);
            await session.SaveAsync(new ManyDoc(b, "b2"), 1);
            await session.CommitAsync();
        }

        await using var read = _store.OpenSession(scope);
        var found = await read.LoadManyAsync<ManyDoc>([a, b, NewId(), a]);

        Assert.Equal(2, found.Count);
        Assert.Equal("a", found[a].Document.Name);
        Assert.Equal(1, found[a].Version);
        Assert.Equal("b2", found[b].Document.Name);
        Assert.Equal(2, found[b].Version);
    }

    [Fact]
    public async Task IsScopeBound_AndSeesTheSessionsOwnUncommittedWrites()
    {
        var mine = ScopeContext.Tenant(Guid.NewGuid());
        var other = ScopeContext.Tenant(Guid.NewGuid());
        var foreignId = NewId();
        await using (var session = _store.OpenSession(other))
        {
            await session.SaveAsync(new ManyDoc(foreignId, "other tenant"), 0);
            await session.CommitAsync();
        }

        await using var write = _store.OpenSession(mine);
        var ownId = NewId();
        await write.SaveAsync(new ManyDoc(ownId, "uncommitted"), 0);

        var found = await write.LoadManyAsync<ManyDoc>([ownId, foreignId]);

        Assert.Equal([ownId], found.Keys);
        await write.DiscardAsync();
    }

    [Fact]
    public async Task NoIds_ReturnsEmpty_InvalidIdThrows()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));

        Assert.Empty(await session.LoadManyAsync<ManyDoc>([]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => session.LoadManyAsync<ManyDoc>([NewId(), ""]));
    }
}
