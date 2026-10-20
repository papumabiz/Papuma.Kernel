// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Gdpr;

/// <summary>
/// <c>RedactEventsAsync</c> selects events by a payload value compared as text (<c>#&gt;&gt;</c>).
/// These cases are the reference for the SQLite kernel (review L-4): its
/// <c>RedactEventsSelectorTests</c> twin runs the identical table, so an erasure request finds
/// the same events on both engines whatever JSON type the selector field has.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RedactEventsSelectorTests
{
    private readonly PostgresFixture _fixture;

    public RedactEventsSelectorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record SelectorEvent(string Name, long Count, bool Flag, decimal Amount, string Note = "secret");

    private sealed record SelectorCase(string Path, string Value, SelectorEvent Target, SelectorEvent Decoy);

    private static readonly SelectorEvent Base = new("base", 0, false, 0m);

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    private static readonly Dictionary<string, SelectorCase> Cases = new()
    {
        ["string"] = new("name", "alice", Base with { Name = "alice" }, Base with { Name = "bob" }),
        ["integer"] = new("count", "42", Base with { Count = 42 }, Base with { Count = 43 }),
        ["integer-beyond-2^53"] = new(
            "count", "9007199254740993", Base with { Count = 9007199254740993 }, Base with { Count = 9007199254740992 }),
        ["negative-integer"] = new("count", "-7", Base with { Count = -7 }, Base with { Count = 7 }),
        ["bool-true"] = new("flag", "true", Base with { Flag = true }, Base with { Flag = false }),
        ["bool-false"] = new("flag", "false", Base with { Flag = false }, Base with { Flag = true }),
        ["decimal-keeps-its-scale"] = new("amount", "1.50", Base with { Amount = 1.50m }, Base with { Amount = 1.5m }),
        ["decimal-fraction"] = new("amount", "0.25", Base with { Amount = 0.25m }, Base with { Amount = 0.5m }),
    };

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task SelectorValue_IsComparedAsText_WhateverTheJsonTypeOfTheField(string caseName)
    {
        var c = Cases[caseName];
        var model = new KernelModelBuilder().Event<SelectorEvent>().Build();
        var store = await _fixture.Database.CreateStoreAsync(model);
        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");

        await using (var session = store.OpenSession(scope))
        {
            await session.AppendAsync(c.Target);
            await session.AppendAsync(c.Decoy);
            await session.CommitAsync();
        }

        await using (var session = store.OpenSession(scope))
        {
            var rewritten = await session.RedactEventsAsync<SelectorEvent>(c.Path, c.Value, ["note"], "erasure-request");
            Assert.Equal(1, rewritten);
            await session.CommitAsync();
        }

        Assert.Equal(1L, await CountWithNoteAsync(scope, present: false));
        Assert.Equal(1L, await CountWithNoteAsync(scope, present: true));
    }

    [Fact]
    public async Task SelectorForAMissingField_MatchesNothing()
    {
        var model = new KernelModelBuilder().Event<SelectorEvent>().Build();
        var store = await _fixture.Database.CreateStoreAsync(model);
        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
        await using var session = store.OpenSession(scope);
        await session.AppendAsync(Base);
        await session.CommitAsync();

        Assert.Equal(0, await session.RedactEventsAsync<SelectorEvent>("nope", "x", ["note"], "erasure-request"));
    }

    private async Task<long> CountWithNoteAsync(ScopeContext scope, bool present)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM papuma.event WHERE tenant_id = @tenantId AND event_type = 'SelectorEvent' " +
            $"AND (payload ? 'note') = {(present ? "true" : "false")}");
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId!);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
