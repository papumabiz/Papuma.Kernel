// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// <c>GetChangesByCorrelationAsync</c>: a unit of work's changes read back as one unit —
/// across document types, in feed order, scope-bound.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CorrelationReadTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public CorrelationReadTests(PostgresFixture fixture)
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
        _store = await _fixture.Database.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ReturnsEveryChangeOfTheUnitOfWork_AcrossTypes_InFeedOrder()
    {
        var tenant = ScopeContext.Tenant(Guid.NewGuid());
        var projectId = NewId();
        await using (var setup = _store.OpenSession(tenant))
        {
            await setup.SaveAsync(new CorrelationProject(projectId, "jejak"), 0);
            await setup.CommitAsync();
        }

        // One command: bump the counter, create the item — like "create a child".
        Guid correlationId;
        var itemId = NewId();
        await using (var command = _store.OpenSession(tenant))
        {
            correlationId = command.CorrelationId;
            var bumped = await command.PatchAsync<CorrelationProject>(projectId, p => p.Increment(x => x.NextNumber));
            await command.SaveAsync(new CorrelationItem(itemId, projectId, bumped.GetDocument<CorrelationProject>().NextNumber - 1), 0);
            await command.CommitAsync();
        }

        await using var reader = _store.OpenSession(tenant);
        var changes = await reader.GetChangesByCorrelationAsync(correlationId);

        Assert.Collection(changes,
            c =>
            {
                Assert.Equal((nameof(CorrelationProject), projectId, ChangeOperation.Update), (c.DocumentType, c.DocumentId, c.Operation));
                Assert.Equal(2, c.Version);
            },
            c => Assert.Equal((nameof(CorrelationItem), itemId, ChangeOperation.Insert), (c.DocumentType, c.DocumentId, c.Operation)));
        Assert.True(changes[0].Seq < changes[1].Seq);
        Assert.All(changes, c => Assert.Equal(correlationId.ToString("N"), (string?)c.Metadata["correlationId"]));
    }

    [Fact]
    public async Task IsScopeBound_AndSeesTheSessionsOwnUncommittedWrites()
    {
        var tenant = ScopeContext.Tenant(Guid.NewGuid());
        await using var session = _store.OpenSession(tenant);
        await session.SaveAsync(new CorrelationProject(NewId(), "uncommitted"), 0);

        var own = await session.GetChangesByCorrelationAsync(session.CorrelationId);
        await session.CommitAsync();

        await using var otherTenant = _store.OpenSession(ScopeContext.Tenant(Guid.NewGuid()));
        var foreign = await otherTenant.GetChangesByCorrelationAsync(session.CorrelationId);
        await using var sameTenant = _store.OpenSession(tenant);
        var unknown = await sameTenant.GetChangesByCorrelationAsync(Guid.NewGuid());

        Assert.Single(own);
        Assert.Empty(foreign);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task LookupShape_CanUseTheCorrelationIndex()
    {
        await using var conn = await _fixture.Database.OwnerDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Same predicate text as GetChangesByCorrelationAsync; seqscan off so a tiny test
        // table cannot hide a mismatch between query and index expression.
        cmd.CommandText = """
            SET LOCAL enable_seqscan = off;
            EXPLAIN SELECT seq FROM papuma.change
            WHERE scope = 'Tenant' AND tenant_id = 't'
              AND metadata ->> 'correlationId' = 'abc'
            ORDER BY seq
            """;
        var plan = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }
        }

        Assert.Contains(plan, line => line.Contains("ix_papuma_change_correlation", StringComparison.Ordinal));
    }
}
