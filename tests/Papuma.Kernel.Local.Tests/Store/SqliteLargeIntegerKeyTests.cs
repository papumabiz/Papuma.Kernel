// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Integer key values are bound as integers (review L-9). Binding every number as a
/// <c>double</c> rounds beyond 2^53: the id <c>9007199254740993</c> became
/// <c>9007199254740992.0</c> and found nothing — or, for <c>DeleteWhereAsync</c> on snowflake
/// ids, the neighbouring document.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteLargeIntegerKeyTests : IAsyncLifetime
{
    private const long Odd = 9007199254740993L;  // 2^53 + 1: not representable as a double
    private const long Even = 9007199254740992L; // 2^53

    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteLargeIntegerKeyTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Snowflake(string Id, [property: LookupKey] long Number, string Label);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Snowflake>().Build();
        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<(SqliteDocumentSession Session, string OddId, string EvenId)> SeedAsync()
    {
        var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var oddId = NewId();
        var evenId = NewId();
        await session.SaveAsync(new Snowflake(oddId, Odd, "odd"), 0);
        await session.SaveAsync(new Snowflake(evenId, Even, "even"), 0);
        return (session, oddId, evenId);
    }

    [Fact]
    public async Task LoadByKeyAsync_FindsExactlyThePrecisionSensitiveValue()
    {
        var (session, oddId, evenId) = await SeedAsync();
        await using var _ = session;

        var odd = await session.LoadByKeyAsync<Snowflake>(x => x.Number, Odd);
        var even = await session.LoadByKeyAsync<Snowflake>(x => x.Number, Even);

        Assert.Equal(oddId, odd?.Document.Id);
        Assert.Equal(evenId, even?.Document.Id);
    }

    [Fact]
    public async Task LoadAllByKeyAsync_FindsExactlyThePrecisionSensitiveValue()
    {
        var (session, oddId, _) = await SeedAsync();
        await using var _ = session;

        var odd = await session.LoadAllByKeyAsync<Snowflake>(x => x.Number, Odd, limit: 10);

        Assert.Equal([oddId], odd.Select(d => d.Document.Id));
    }

    [Fact]
    public async Task DeleteWhereAsync_DeletesOnlyTheExactValue_NotTheNeighbour()
    {
        var (session, oddId, evenId) = await SeedAsync();
        await using var _ = session;

        var result = await session.DeleteWhereAsync<Snowflake>(x => x.Number, Odd);

        Assert.Equal(1, result.Count);
        Assert.Null(await session.LoadAsync<Snowflake>(oddId));
        Assert.NotNull(await session.LoadAsync<Snowflake>(evenId)); // the neighbour must survive
    }

    [Fact]
    public async Task PatchWhereAsync_PatchesOnlyTheExactValue()
    {
        var (session, oddId, evenId) = await SeedAsync();
        await using var _ = session;

        var result = await session.PatchWhereAsync<Snowflake>(x => x.Number, Odd, p => p.Set(x => x.Label, "patched"));

        Assert.Equal(1, result.Count);
        Assert.Equal("patched", (await session.LoadAsync<Snowflake>(oddId))!.Document.Label);
        Assert.Equal("even", (await session.LoadAsync<Snowflake>(evenId))!.Document.Label);
    }

    [Fact]
    public async Task IntegralDecimalAndFloatingKeys_StillMatch()
    {
        // Floating-point and decimal values keep matching numerically (42 = 42.0 in SQLite).
        var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        await using var _ = session;
        var id = NewId();
        await session.SaveAsync(new Snowflake(id, 42, "answer"), 0);

        Assert.Equal(id, (await session.LoadByKeyAsync<Snowflake>(x => x.Number, 42))?.Document.Id);   // int
        Assert.Equal(id, (await session.LoadByKeyAsync<Snowflake>(x => x.Number, 42m))?.Document.Id);  // decimal
        Assert.Equal(id, (await session.LoadByKeyAsync<Snowflake>(x => x.Number, 42.0))?.Document.Id); // double
    }
}
