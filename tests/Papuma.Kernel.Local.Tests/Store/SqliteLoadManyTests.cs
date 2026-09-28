// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>Parity with <c>LoadManyTests</c> (feedback F-17).</summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteLoadManyTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteLoadManyTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record ManyDoc(string Id, string Name);

    public async Task InitializeAsync()
    {
        _store = await _fixture.CreateStoreAsync(new KernelModelBuilder().Document<ManyDoc>().Build());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task LoadsTheFoundDocuments_ScopeBound_MissingAbsent_DuplicatesOnce()
    {
        var mine = ScopeContext.Tenant(Guid.NewGuid());
        var other = ScopeContext.Tenant(Guid.NewGuid());
        var a = NewId();
        var b = NewId();
        var foreign = NewId();
        await using (var session = _store.OpenSession(mine))
        {
            await session.SaveAsync(new ManyDoc(a, "a"), 0);
            await session.SaveAsync(new ManyDoc(b, "b"), 0);
            await session.SaveAsync(new ManyDoc(b, "b2"), 1);
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(other))
        {
            await session.SaveAsync(new ManyDoc(foreign, "other tenant"), 0);
            await session.CommitAsync();
        }

        await using var read = _store.OpenSession(mine);
        var found = await read.LoadManyAsync<ManyDoc>([a, b, foreign, NewId(), a]);

        Assert.Equal(2, found.Count);
        Assert.Equal("a", found[a].Document.Name);
        Assert.Equal("b2", found[b].Document.Name);
        Assert.Equal(2, found[b].Version);
        Assert.Empty(await read.LoadManyAsync<ManyDoc>([]));
    }
}
