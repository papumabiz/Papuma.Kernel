// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Argument validation at the session boundary (review S-10): a bad argument fails with the
/// right parameter name, before the session touches the database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionGuardClauseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record GuardDoc(string Id, string Name, string Status);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<GuardDoc>(d => d.LookupKey(x => x.Status)).Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private DocumentSession OpenSession() => _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PatchAsync_RejectsAnExpectedVersionBelowOne(long expectedVersion)
    {
        await using var session = OpenSession();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.PatchAsync<GuardDoc>(
            "x", p => p.Set(d => d.Name, "n"), expectedVersion));

        Assert.Equal("expectedVersion", ex.ParamName);
    }

    [Fact]
    public async Task PatchAsync_AcceptsNoExpectedVersion_AndAValidOne()
    {
        await using var session = OpenSession();
        await session.SaveAsync(new GuardDoc("x", "a", "s"), 0);

        await session.PatchAsync<GuardDoc>("x", p => p.Set(d => d.Name, "b"));
        await session.PatchAsync<GuardDoc>("x", p => p.Set(d => d.Name, "c"), expectedVersion: 2);

        Assert.Equal("c", (await session.LoadAsync<GuardDoc>("x"))!.Document.Name);
    }

    [Fact]
    public async Task PatchWhereAsync_RejectsANullValue()
    {
        await using var session = OpenSession();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => session.PatchWhereAsync<GuardDoc>(
            d => d.Status, null!, p => p.Set(d => d.Name, "n")));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public async Task DeleteWhereAsync_RejectsANullValue()
    {
        await using var session = OpenSession();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.DeleteWhereAsync<GuardDoc>(d => d.Status, null!));

        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData(0L, null, "fromVersion")]
    [InlineData(-3L, null, "fromVersion")]
    [InlineData(null, 0L, "toVersion")]
    [InlineData(null, -1L, "toVersion")]
    [InlineData(5L, 2L, "toVersion")]
    public async Task GetHistoryAsync_NamesTheOffendingBound(long? fromVersion, long? toVersion, string expectedParam)
    {
        await using var session = OpenSession();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.GetHistoryAsync<GuardDoc>("x", fromVersion, toVersion));

        Assert.Equal(expectedParam, ex.ParamName);
    }

    [Fact]
    public async Task GetHistoryAsync_AcceptsOpenAndEqualBounds()
    {
        await using var session = OpenSession();
        await session.SaveAsync(new GuardDoc("x", "a", "s"), 0);

        Assert.Single(await session.GetHistoryAsync<GuardDoc>("x"));
        Assert.Single(await session.GetHistoryAsync<GuardDoc>("x", fromVersion: 1, toVersion: 1));
    }
}
