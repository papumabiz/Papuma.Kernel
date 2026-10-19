// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// A declared logical name (ADR-028) keeps documents findable when the class is renamed or moved — the
/// failure it exists to prevent: the class name changes, <c>document_type</c> no longer matches, and every
/// stored document silently disappears. Proven against a real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RenameSafetyTests(PostgresFixture fixture)
{
    [DocumentType("renamed_customer")]
    private sealed record CustomerV1(string Id, string Name);

    // The same documents after a refactoring: another class name, the same declared name.
    [DocumentType("renamed_customer")]
    private sealed record ClientV2(string Id, string Name, string? Note = null);

    private sealed record UndeclaredV1(string Id, string Name);

    private async Task<DocumentStore> StoreFor<T>() where T : class
    {
        var model = new KernelModelBuilder().Document<T>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        return new DocumentStore(fixture.DataSource, model);
    }

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task ARenamedClass_WithADeclaredName_FindsTheDocumentsItsPredecessorStored()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await using (var old = (await StoreFor<CustomerV1>()).OpenSession(tenant))
        {
            await old.SaveAsync(new CustomerV1(id, "Harald"), 0);
            await old.CommitAsync();
        }

        await using var session = (await StoreFor<ClientV2>()).OpenSession(tenant);
        var found = await session.LoadAsync<ClientV2>(id);

        Assert.NotNull(found);
        Assert.Equal("Harald", found.Document.Name);
        await session.SaveAsync(found.Document with { Note = "after the rename" }, found.Version);
        Assert.Equal(2, (await session.LoadAsync<ClientV2>(id))!.Version);
    }

    [Fact]
    public async Task TheDeclaredName_IsWhatIsStored()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await using var session = (await StoreFor<CustomerV1>()).OpenSession(tenant);
        await session.SaveAsync(new CustomerV1(id, "Harald"), 0);
        await session.CommitAsync();

        await using var cmd = fixture.DataSource.CreateCommand("SELECT document_type FROM papuma.document WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);

        Assert.Equal("renamed_customer", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task WithoutADeclaredName_TheTypeIsStoredUnderTheClassName()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await using var session = (await StoreFor<UndeclaredV1>()).OpenSession(tenant);
        await session.SaveAsync(new UndeclaredV1(id, "Harald"), 0);
        await session.CommitAsync();

        await using var cmd = fixture.DataSource.CreateCommand("SELECT document_type FROM papuma.document WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);

        Assert.Equal("UndeclaredV1", (string?)await cmd.ExecuteScalarAsync());
    }
}
