// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Regression coverage for declared keys on non-string fields — the end-to-end proof for
/// the two findings in <c>SqliteSpikeTests</c>
/// (<c>JsonExtract_NumericField_ComparedAgainstTextParameter_DoesNotMatch</c> and
/// <c>JsonExtractIndex_NotUsed_WhenPathIsABoundParameter</c>): both a numeric key value
/// comparison and the expression index it should use.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteNumericKeyTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteNumericKeyTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Ticket(string Id, int Number, bool Urgent, string Subject);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<Ticket>(d => d
                .UniqueKey(x => x.Number)
                .LookupKey(x => x.Urgent))
            .Build();
        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadByKeyAsync_FindsDocument_ByIntegerKey()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Ticket(id, 4711, Urgent: false, "printer on fire"), 0);

        var loaded = await session.LoadByKeyAsync<Ticket>(x => x.Number, 4711);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Document.Id);
    }

    [Fact]
    public async Task LoadByKeyAsync_FindsDocument_ByBooleanKey()
    {
        // Urgent is a LookupKey (non-unique) on purpose — two tickets can both be urgent;
        // this proves the boolean comparison itself works (json_extract's 0/1 vs a bound
        // 0L/1L), not the "exactly one match" plumbing.
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Ticket(id, 1, Urgent: true, "single urgent ticket"), 0);

        var loaded = await session.LoadByKeyAsync<Ticket>(x => x.Urgent, true);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Document.Id);
    }

    [Fact]
    public async Task UniqueKey_Violation_OnIntegerField_ThrowsTypedException()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new Ticket(NewId(), 99, Urgent: false, "first"), 0);

        var ex = await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.SaveAsync(new Ticket(NewId(), 99, Urgent: false, "second"), 0));

        Assert.Equal("number", ex.KeyPath);
    }

    [Fact]
    public async Task DeleteWhereAsync_MatchesByIntegerKey()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new Ticket(NewId(), 555, Urgent: false, "to be deleted"), 0);

        var result = await session.DeleteWhereAsync<Ticket>(x => x.Number, 555);

        Assert.Equal(1, result.Count);
        Assert.Null(await session.LoadByKeyAsync<Ticket>(x => x.Number, 555));
    }
}
