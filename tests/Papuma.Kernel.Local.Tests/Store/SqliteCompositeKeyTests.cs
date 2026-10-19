// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Composite keys on SQLite (ADR-020): the same semantics as on PostgreSQL, backed by a
/// multi-expression <c>json_extract</c> index that the lookup query actually uses.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteCompositeKeyTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteCompositeKeyTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record CompositeTicket(string Id, string ProjectId, int? Number, string Title);

    private sealed record SingleTicket(string Id, string Reference);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<CompositeTicket>(d => d.UniqueKey(x => new { x.ProjectId, x.Number }))
            .Document<SingleTicket>(d => d.UniqueKey(x => x.Reference))
            .Build();
        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant(Guid.NewGuid());

    [Fact]
    public async Task SameCombination_Violates_OtherCombinationsDoNot()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 42, "first"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p2", 42, "other project"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", null, "draft"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", null, "draft"), 0);

        var ex = await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.SaveAsync(new CompositeTicket(NewId(), "p1", 42, "second"), 0));

        Assert.Equal("projectId,number", ex.KeyPath);
        Assert.Equal(["projectId", "number"], ex.KeyPaths);
    }

    [Fact]
    public async Task LoadByKey_WithOneValuePerComponent_FindsTheDocument()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new CompositeTicket(id, "p1", 42, "the one"), 0);
        await session.SaveAsync(new CompositeTicket(NewId(), "p1", 43, "neighbour"), 0);

        var loaded = await session.LoadByKeyAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, ["p1", 42]);

        Assert.Equal(id, loaded!.Document.Id);
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadByKeyAsync<CompositeTicket>(x => new { x.ProjectId, x.Number }, (object)"p1"));
    }

    [Fact]
    public async Task LookupQueryShape_UsesTheCompositeIndex()
    {
        var plan = await ExplainAsync(
            "CompositeTicket", [["projectId"], ["number"]], ("value0", "p1"), ("value1", 42.0));

        Assert.Contains(plan, line => line.Contains("ux_papuma_doc_compositeticket_projectid__number", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LookupQueryShape_UsesTheSingleKeyIndex()
    {
        var plan = await ExplainAsync("SingleTicket", [["reference"]], ("value0", "r1"));

        Assert.Contains(plan, line => line.Contains("papuma_doc_singleticket_reference", StringComparison.Ordinal));
    }

    /// <summary>EXPLAINs the exact text <c>LoadByKeyCoreAsync</c> executes, not a rebuilt copy.</summary>
    private async Task<List<string>> ExplainAsync(
        string documentType, IReadOnlyList<string[]> segments, params (string Name, object Value)[] values)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(_fixture.ConnectionString);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + SqliteDocumentSession.BuildLoadByKeySql(documentType, segments);
        cmd.Parameters.AddWithValue("scope", "Tenant");
        cmd.Parameters.AddWithValue("tenantId", "t");
        // @type is deliberately left unbound: key indexes are partial (WHERE document_type = '…'),
        // and only a literal in the SQL text lets the planner match them statically. A bound
        // value would let SQLite re-prepare with the value peeked and hide a regression.
        foreach (var (name, value) in values)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var plan = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        return plan;
    }
}
