// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// The structural limit of 64 levels through a real PostgreSQL session: every way a document can get too deep —
/// a save, a patch whose result is too deep, a patch value too deep on its own, a reference cycle — fails with
/// <see cref="DocumentTooDeepException"/> and changes nothing; a document exactly at the limit round-trips.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentDepthLimitTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    public sealed record Tree(string Id, Node? Root);

    public sealed record Node(int V, Node? Child);

    public sealed class Loop
    {
        public string Id { get; set; } = string.Empty;

        public Loop? Next { get; set; }
    }

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Tree>().Document<Loop>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // A Tree whose root chain has `nodes` nodes is 1 + nodes levels deep.
    private static Node Chain(int nodes)
    {
        Node? node = null;
        for (var i = 0; i < nodes; i++)
        {
            node = new Node(i, node);
        }

        return node!;
    }

    private DocumentSession Open() => _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));

    [Fact]
    public async Task ADocumentAtTheLimit_RoundTrips_OneLevelMore_IsRefusedAndNotStored()
    {
        await using var session = Open();
        var atLimit = Guid.NewGuid().ToString("N");
        var tooDeep = Guid.NewGuid().ToString("N");

        await session.SaveAsync(new Tree(atLimit, Chain(DocumentTooDeepException.MaxDepth - 1)), 0);
        var ex = await Assert.ThrowsAsync<DocumentTooDeepException>(
            () => session.SaveAsync(new Tree(tooDeep, Chain(DocumentTooDeepException.MaxDepth)), 0));

        Assert.Equal(nameof(Tree), ex.DocumentType);
        Assert.Equal(tooDeep, ex.DocumentId);
        Assert.NotNull(await session.LoadAsync<Tree>(atLimit));
        Assert.Null(await session.LoadAsync<Tree>(tooDeep));
    }

    [Fact]
    public async Task APatchWhoseResultIsTooDeep_IsRefused_AndTheDocumentStaysAsItWas()
    {
        await using var session = Open();
        var id = Guid.NewGuid().ToString("N");
        await session.SaveAsync(new Tree(id, Chain(10)), 0);

        // 60 levels set 10 levels down: each part is fine, the result is not.
        var ex = await Assert.ThrowsAsync<DocumentTooDeepException>(() => session.PatchAsync<Tree>(id,
            p => p.Set(x => x.Root!.Child!.Child!.Child!.Child!.Child!.Child!.Child!.Child!.Child, Chain(60))));

        Assert.Equal(nameof(Tree), ex.DocumentType);
        Assert.Equal(id, ex.DocumentId);
        var loaded = await session.LoadAsync<Tree>(id);
        Assert.Equal(1, loaded!.Version);
        Assert.Equal(new Tree(id, Chain(10)), loaded.Document);
    }

    [Fact]
    public async Task APatchValueTooDeepOnItsOwn_IsRefusedBeforeAnythingIsSent()
    {
        await using var session = Open();
        var id = Guid.NewGuid().ToString("N");
        await session.SaveAsync(new Tree(id, Chain(1)), 0);

        await Assert.ThrowsAsync<DocumentTooDeepException>(
            () => session.PatchAsync<Tree>(id, p => p.Set(x => x.Root, Chain(70))));

        Assert.Equal(1, (await session.LoadAsync<Tree>(id))!.Version);
    }

    [Fact]
    public async Task AReferenceCycle_IsReportedAsTooDeep()
    {
        await using var session = Open();
        var loop = new Loop { Id = Guid.NewGuid().ToString("N") };
        loop.Next = loop;

        var ex = await Assert.ThrowsAsync<DocumentTooDeepException>(() => session.SaveAsync(loop, 0));

        Assert.Contains("cycle", ex.Message, StringComparison.Ordinal);
    }
}
