// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// PostgreSQL computes an <c>Increment</c> in <c>numeric</c>: values beyond the range of a <see cref="decimal"/>
/// work, where the in-process (SQLite) engine throws <see cref="PatchValueOutOfRangeException"/> — the divergence
/// documented in <c>docs/spec/patch.md</c> (review C-4). The in-process re-check of the targets must not trip over it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PatchRangeTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record Meter(string Id, double Reading = 0, decimal Balance = 0);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Meter>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task Patch_IncrementOnTheTopOfTheDecimalRange_Works_WithoutAnOverflowException()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Meter(id, Balance: decimal.MaxValue), 0);

        var result = await session.PatchAsync<Meter>(id, p => p.Increment(x => x.Balance));

        // Not loaded back: 7.9e28 + 1 no longer fits the document's decimal property — PostgreSQL stored it exactly.
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task PatchMany_IncrementOnTheTopOfTheDecimalRange_Works()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Meter(id, Balance: decimal.MaxValue), 0);

        var result = await session.PatchManyAsync<Meter>([id], p => p.Increment(x => x.Balance));

        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task Patch_IncrementOnANumberBeyondTheDecimalRange_Works()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Meter(id, Reading: 1e30), 0);

        var result = await session.PatchAsync<Meter>(id, p => p.Increment(x => x.Reading));

        Assert.Equal(2, result.Version);
    }
}
