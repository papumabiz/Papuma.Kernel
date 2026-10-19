// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// A declared logical name (ADR-028) keeps documents findable when the class is renamed or moved —
/// the SQLite counterpart of <c>RenameSafetyTests</c>.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteRenameSafetyTests(SqliteFixture fixture)
{
    [DocumentType("renamed_customer")]
    private sealed record CustomerV1(string Id, string Name);

    [DocumentType("renamed_customer")]
    private sealed record ClientV2(string Id, string Name, string? Note = null);

    private sealed record UndeclaredV1(string Id, string Name);

    private Task<SqliteDocumentStore> StoreFor<T>() where T : class =>
        fixture.CreateStoreAsync(new KernelModelBuilder().Document<T>().Build());

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private async Task<string?> StoredTypeAsync(string id)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(fixture.ConnectionString);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT document_type FROM document WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return (string?)await cmd.ExecuteScalarAsync();
    }

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
    public async Task TheDeclaredName_IsWhatIsStored_AndAnUndeclaredTypeUsesTheClassName()
    {
        var id = Guid.NewGuid().ToString("N");
        var otherId = Guid.NewGuid().ToString("N");
        await using (var declared = (await StoreFor<CustomerV1>()).OpenSession(NewTenant()))
        {
            await declared.SaveAsync(new CustomerV1(id, "Harald"), 0);
            await declared.CommitAsync();
        }

        await using (var undeclared = (await StoreFor<UndeclaredV1>()).OpenSession(NewTenant()))
        {
            await undeclared.SaveAsync(new UndeclaredV1(otherId, "Harald"), 0);
            await undeclared.CommitAsync();
        }

        Assert.Equal("renamed_customer", await StoredTypeAsync(id));
        Assert.Equal("UndeclaredV1", await StoredTypeAsync(otherId));
    }
}
