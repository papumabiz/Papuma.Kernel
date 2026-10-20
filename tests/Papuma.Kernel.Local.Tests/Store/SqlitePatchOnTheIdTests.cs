// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// SQLite twin of <c>PatchOnTheIdTests</c> (review S-1): a patch must not change the property that carries the
/// document id — the <c>id</c> column and <c>data.id</c> would drift apart.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqlitePatchOnTheIdTests(SqliteFixture fixture)
{
    private sealed record IdPatchDoc(string Id, string Name, [property: LookupKey] string Group);

    private sealed record CustomIdPatchDoc(string Code, string Name);

    private async Task<SqliteDocumentStore> StoreAsync() =>
        await fixture.CreateStoreAsync(new KernelModelBuilder()
            .Document<IdPatchDoc>()
            .Document<CustomIdPatchDoc>(d => d.HasId(x => x.Code))
            .Build());

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Set_OnTheIdProperty_IsRefused_AndTheDocumentIsUntouched()
    {
        var store = await StoreAsync();
        await using var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = NewId();
        await session.SaveAsync(new IdPatchDoc(id, "original", "g"), 0);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => session.PatchAsync<IdPatchDoc>(id, p => p.Set(x => x.Id, "other")));

        Assert.Equal("patch", ex.ParamName);
        var loaded = await session.LoadAsync<IdPatchDoc>(id);
        Assert.Equal(id, loaded!.Document.Id);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Remove_OnTheIdProperty_IsRefused()
    {
        var store = await StoreAsync();
        await using var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = NewId();
        await session.SaveAsync(new IdPatchDoc(id, "original", "g"), 0);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => session.PatchAsync<IdPatchDoc>(id, p => p.Remove(x => x.Id)));

        Assert.Equal("patch", ex.ParamName);
    }

    [Fact]
    public async Task WithACustomId_ItIsTheCustomIdProperty_ThatIsProtected()
    {
        var store = await StoreAsync();
        await using var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var code = NewId();
        await session.SaveAsync(new CustomIdPatchDoc(code, "original"), 0);

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.PatchAsync<CustomIdPatchDoc>(code, p => p.Set(x => x.Code, "other")));

        await session.PatchAsync<CustomIdPatchDoc>(code, p => p.Set(x => x.Name, "renamed"));
        Assert.Equal("renamed", (await session.LoadAsync<CustomIdPatchDoc>(code))!.Document.Name);
    }

    [Fact]
    public async Task PatchMany_AndPatchWhere_RefuseTheIdToo()
    {
        var store = await StoreAsync();
        await using var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = NewId();
        await session.SaveAsync(new IdPatchDoc(id, "original", "g"), 0);

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.PatchManyAsync<IdPatchDoc>([id], p => p.Set(x => x.Id, "other")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.PatchWhereAsync<IdPatchDoc>(x => x.Group, "g", p => p.Remove(x => x.Id)));

        Assert.Equal(id, (await session.LoadAsync<IdPatchDoc>(id))!.Document.Id);
    }

    [Fact]
    public async Task APatchOfOtherFields_StillWorks()
    {
        var store = await StoreAsync();
        await using var session = store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = NewId();
        await session.SaveAsync(new IdPatchDoc(id, "original", "g"), 0);

        var result = await session.PatchAsync<IdPatchDoc>(id, p => p.Set(x => x.Name, "renamed"));

        Assert.Equal(2, result.Version);
    }
}
