// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// <c>RedactEventsAsync</c> selects events by a payload value compared as text, like the
/// PostgreSQL kernel's <c>#&gt;&gt;</c> (review L-4). SQLite's <c>json_extract</c> returns
/// INTEGER/REAL for JSON numbers and 0/1 for booleans, which never equal a TEXT parameter —
/// an erasure request for a numeric user id silently matched nothing and left personal data in
/// the event log. This is the same case table as the PostgreSQL kernel's
/// <c>Papuma.Kernel.Tests.Gdpr.RedactEventsSelectorTests</c>.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteRedactEventsSelectorTests
{
    private readonly SqliteFixture _fixture;

    public SqliteRedactEventsSelectorTests(SqliteFixture fixture)
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
        var store = await _fixture.CreateStoreAsync(model);
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
        var store = await _fixture.CreateStoreAsync(model);
        var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
        await using var session = store.OpenSession(scope);
        await session.AppendAsync(Base);
        await session.CommitAsync();

        Assert.Equal(0, await session.RedactEventsAsync<SelectorEvent>("nope", "x", ["note"], "erasure-request"));
    }

    private async Task<long> CountWithNoteAsync(ScopeContext scope, bool present)
    {
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM event WHERE tenant_id = @tenantId AND event_type = 'SelectorEvent' " +
            $"AND (json_type(payload, '$.note') IS NOT NULL) = {(present ? 1 : 0)}";
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId!);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
