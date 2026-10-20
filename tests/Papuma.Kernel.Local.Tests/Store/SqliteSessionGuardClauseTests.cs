// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Argument validation at the SQLite session boundary (review L-12, L-14) — the counterpart of the
/// PostgreSQL <c>SessionGuardClauseTests</c>: a bad argument fails with the right parameter name,
/// before the session touches the database.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteSessionGuardClauseTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    private sealed record GuardDoc(string Id, string Name, string Status);

    private sealed record GuardEvent(string UserId, string Note);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<GuardDoc>(d => d.LookupKey(x => x.Status))
            .Event<GuardEvent>()
            .Build();
        _store = await fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private SqliteDocumentSession OpenSession() => _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));

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
    public async Task PatchWhereAsync_RejectsANullValue_BeforeAnythingIsOpened()
    {
        await using var session = OpenSession();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => session.PatchWhereAsync<GuardDoc>(
            d => d.Status, null!, p => p.Set(d => d.Name, "n")));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public async Task DeleteWhereAsync_RejectsANullValue_BeforeAnythingIsOpened()
    {
        await using var session = OpenSession();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.DeleteWhereAsync<GuardDoc>(d => d.Status, null!));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public async Task PatchWhereAndDeleteWhere_StillWorkWithAValue()
    {
        await using var session = OpenSession();
        await session.SaveAsync(new GuardDoc("x", "a", "open"), 0);
        await session.SaveAsync(new GuardDoc("y", "b", "closed"), 0);

        var patched = await session.PatchWhereAsync<GuardDoc>(d => d.Status, "open", p => p.Set(d => d.Name, "z"));
        var deleted = await session.DeleteWhereAsync<GuardDoc>(d => d.Status, "closed");

        Assert.Equal(1, patched.Count);
        Assert.Equal(1, deleted.Count);
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
        Assert.Single(await session.GetHistoryAsync<GuardDoc>("x", fromVersion: 1));
    }

    // A selector path with a quote or backslash was reported as "path", a name no caller ever passed.
    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\\\\b")]
    public async Task RedactEventsAsync_NamesTheSelectorPathAsTheOffendingArgument(string selectorPath)
    {
        await using var session = OpenSession();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => session.RedactEventsAsync<GuardEvent>(
            selectorPath, "u1", ["note"], "erasure"));

        Assert.Equal("selectorPath", ex.ParamName);
    }

    [Fact]
    public async Task RedactEventsAsync_AcceptsAPlainSelectorPath()
    {
        await using var session = OpenSession();
        await session.AppendAsync(new GuardEvent("u1", "secret"));

        var rewritten = await session.RedactEventsAsync<GuardEvent>("userId", "u1", ["note"], "erasure");

        Assert.Equal(1, rewritten);
    }
}
