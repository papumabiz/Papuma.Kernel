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

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<CompositeTicket>(d => d.UniqueKey(x => new { x.ProjectId, x.Number }))
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
        await using var connection = await SqliteConnectionFactory.OpenAsync(_fixture.ConnectionString);
        await using var cmd = connection.CreateCommand();
        // The shape LoadByKeyCoreAsync generates: literal paths, textually identical to the index.
        cmd.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT id FROM document
            WHERE scope = 'Tenant' AND tenant_id = 't' AND document_type = 'CompositeTicket'
              AND json_extract(data, '$.projectId') = @value0 AND json_extract(data, '$.number') = @value1
            """;
        cmd.Parameters.AddWithValue("value0", "p1");
        cmd.Parameters.AddWithValue("value1", 42.0);

        var plan = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, line => line.Contains("ux_papuma_doc_compositeticket_projectid__number", StringComparison.Ordinal));
    }
}
