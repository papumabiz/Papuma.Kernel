// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// <c>GetChangesByCorrelationAsync</c> on SQLite: same semantics as the PostgreSQL
/// kernel, backed by a <c>json_extract</c> index the lookup query actually uses.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteCorrelationReadTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteCorrelationReadTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record CorrelationProject(string Id, string Name, int NextNumber = 1);

    private sealed record CorrelationItem(string Id, string ProjectId, int Number);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<CorrelationProject>()
            .Document<CorrelationItem>()
            .Build();
        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ReturnsEveryChangeOfTheUnitOfWork_InFeedOrder_ScopeBound()
    {
        var tenant = ScopeContext.Tenant(Guid.NewGuid());
        var projectId = NewId();
        var itemId = NewId();
        Guid correlationId;
        await using (var command = _store.OpenSession(tenant))
        {
            correlationId = command.CorrelationId;
            await command.SaveAsync(new CorrelationProject(projectId, "jejak"), 0);
            await command.SaveAsync(new CorrelationItem(itemId, projectId, 1), 0);
            await command.PatchAsync<CorrelationProject>(projectId, p => p.Increment(x => x.NextNumber));
            await command.CommitAsync();
        }

        // One session at a time: the SQLite kernel is single-writer per file.
        IReadOnlyList<ChangeRecord> changes, foreign;
        await using (var reader = _store.OpenSession(tenant))
        {
            changes = await reader.GetChangesByCorrelationAsync(correlationId);
        }

        await using (var otherTenant = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid())))
        {
            foreign = await otherTenant.GetChangesByCorrelationAsync(correlationId);
        }

        Assert.Equal(
            [
                (nameof(CorrelationProject), projectId, ChangeOperation.Insert),
                (nameof(CorrelationItem), itemId, ChangeOperation.Insert),
                (nameof(CorrelationProject), projectId, ChangeOperation.Update),
            ],
            changes.Select(c => (c.DocumentType, c.DocumentId, c.Operation)));
        Assert.Equal(changes.OrderBy(c => c.Seq).Select(c => c.Seq), changes.Select(c => c.Seq));
        Assert.Empty(foreign);
    }

    [Fact]
    public async Task LookupShape_UsesTheCorrelationIndex()
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(_fixture.ConnectionString);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT seq FROM change
            WHERE scope = 'Tenant' AND tenant_id = 't'
              AND json_extract(metadata, '$.correlationId') = @correlationId
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("correlationId", "abc");

        var plan = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, line => line.Contains("ix_change_correlation", StringComparison.Ordinal));
    }
}
