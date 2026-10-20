// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// An <c>Increment</c> beyond what the in-process engine computes exactly fails typed and changes nothing
/// (review C-4); PostgreSQL computes in <c>numeric</c> and accepts it — the divergence of <c>docs/spec/patch.md</c>.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqlitePatchRangeTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    private sealed record Meter(string Id, double Reading = 0, decimal Balance = 0);

    public async Task InitializeAsync() =>
        _store = await fixture.CreateStoreAsync(new KernelModelBuilder().Document<Meter>().Build());

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task Patch_IncrementOnANumberBeyondTheDecimalRange_ThrowsTyped_AndLeavesTheDocumentUntouched()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Meter(id, Reading: 1e30), 0);

        var ex = await Assert.ThrowsAsync<PatchValueOutOfRangeException>(
            () => session.PatchAsync<Meter>(id, p => p.Increment(x => x.Reading)));

        Assert.Equal(nameof(Meter), ex.DocumentType);
        Assert.Equal(id, ex.DocumentId);
        Assert.Equal("reading", ex.FieldPath);
        var loaded = await session.LoadAsync<Meter>(id);
        Assert.Equal(1e30, loaded!.Document.Reading);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Patch_IncrementThatOverflowsTheDecimalRange_ThrowsTyped_NotOverflowException()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Meter(id, Balance: decimal.MaxValue), 0);

        await Assert.ThrowsAsync<PatchValueOutOfRangeException>(
            () => session.PatchAsync<Meter>(id, p => p.Increment(x => x.Balance)));

        Assert.Equal(decimal.MaxValue, (await session.LoadAsync<Meter>(id))!.Document.Balance);
    }

    [Fact]
    public async Task PatchMany_IncrementBeyondTheRange_ThrowsTyped_AndRollsBackTheWholeBatch()
    {
        await using var session = _store.OpenSession(NewTenant());
        var fine = NewId();
        var huge = NewId();
        await session.SaveAsync(new Meter(fine, Balance: 1), 0);
        await session.SaveAsync(new Meter(huge, Balance: decimal.MaxValue), 0);

        await Assert.ThrowsAsync<PatchValueOutOfRangeException>(
            () => session.PatchManyAsync<Meter>([fine, huge], p => p.Increment(x => x.Balance)));

        Assert.Equal(1, (await session.LoadAsync<Meter>(fine))!.Document.Balance);
        Assert.Equal(1, (await session.LoadAsync<Meter>(fine))!.Version);
    }

    [Fact]
    public async Task Patch_IncrementNearTheTopOfTheRange_StaysExact()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Meter(id, Balance: decimal.MaxValue - 1), 0);

        await session.PatchAsync<Meter>(id, p => p.Increment(x => x.Balance));

        Assert.Equal(decimal.MaxValue, (await session.LoadAsync<Meter>(id))!.Document.Balance);
    }
}
