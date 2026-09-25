// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Composite keys (ADR-020): uniqueness over several fields as one multi-column partial
/// expression index, lookup with one value per component, and the single-field-only
/// operations refusing them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CompositeKeyTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public CompositeKeyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record CompositeTicket(string Id, string ProjectId, int? Number, string Title, string Status = "open");

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<CompositeTicket>(d => d
                .UniqueKey(x => new { x.ProjectId, x.Number })
                .LookupKey(x => x.Status))
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant(Guid.NewGuid());

    [Fact]
    public async Task SameCombination_Violates_WithBothComponentPaths()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 42, "first"), 0);

        var ex = await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.SaveAsync(new CompositeTicket(NewId(), "p1", 42, "second"), 0));

        Assert.Equal("CompositeTicket", ex.DocumentType);
        Assert.Equal("projectId,number", ex.KeyPath);
        Assert.Equal(["projectId", "number"], ex.KeyPaths);
    }

    [Fact]
    public async Task EachComponentAlone_MayRepeat()
    {
        await using var session = _store.OpenSession(NewTenant());

        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 1, "a"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p2", 1, "same number, other project"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 2, "same project, other number"), 0);
    }

    [Fact]
    public async Task SameCombination_InAnotherTenant_DoesNotConflict()
    {
        await using (var sessionA = _store.OpenSession(NewTenant()))
        {
            await sessionA.SaveAsync(new CompositeTicket(NewId(), "p1", 7, "a"), 0);
            await sessionA.CommitAsync();
        }

        await using var sessionB = _store.OpenSession(NewTenant());
        await sessionB.SaveAsync(new CompositeTicket(NewId(), "p1", 7, "b"), 0);
    }

    [Fact]
    public async Task MissingComponent_IsNotEnforced()
    {
        await using var session = _store.OpenSession(NewTenant());

        await session.SaveAsync(new CompositeTicket(NewId(), "p1", null, "draft 1"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", null, "draft 2"), 0);
    }

    [Fact]
    public async Task Patch_IntoAnExistingCombination_Violates()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 1, "a"), 0);
        var id = NewId();
        await session.SaveAsync(new CompositeTicket(id, "p1", 2, "b"), 0);

        var ex = await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.PatchAsync<CompositeTicket>(id, p => p.Set(x => x.Number, 1)));

        Assert.Equal("projectId,number", ex.KeyPath);
    }

    [Fact]
    public async Task LoadByKey_WithOneValuePerComponent_FindsTheDocument()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new CompositeTicket(id, "p1", 42, "the one"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 43, "neighbour"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p2", 42, "other project"), 0);

        var loaded = await session.LoadByKeyAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, ["p1", 42]);
        var missing = await session.LoadByKeyAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, ["p3", 42]);

        Assert.Equal(id, loaded!.Document.Id);
        Assert.Null(missing);
    }

    [Fact]
    public async Task LoadByKey_RejectsWrongValueCount_AndComponentOrder()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadByKeyAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, ["p1"]));
        // The key is declared (projectId, number); the reverse order is a different, undeclared key.
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadByKeyAsync<CompositeTicket>(x => new { x.Number, x.ProjectId }, [42, "p1"]));
    }

    [Fact]
    public async Task SingleFieldOperations_RefuseTheCompositeKey()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadByKeyAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, (object)"p1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.PatchWhereAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, "p1", p => p.Set(x => x.Title, "t")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.DeleteWhereAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, "p1"));
    }

    [Fact]
    public async Task SingleFieldKeys_KeepWorking_NextToTheCompositeKey()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 1, "a", Status: "done"), 0);

        var result = await session.PatchWhereAsync<CompositeTicket>(x => x.Status, "done", p => p.Set(x => x.Status, "archived"));

        Assert.Equal(1, result.Count);
    }
}
