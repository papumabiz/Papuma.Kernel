// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Tenancy;

/// <summary>
/// The scope predicates for application tables (ADR-019): <c>papuma.scope_visible</c> and
/// <c>papuma.scope_writable</c> must decide exactly like the kernel's own RLS policies,
/// and an application table guarded by them must isolate like <c>papuma.*</c> does —
/// the runnable form of the same-database read-model recipe.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeFunctionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public ScopeFunctionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Ticket(string Id, string Title);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Ticket>().Build();
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        await _fixture.GrantAppRoleAccessAsync();
        _store = new DocumentStore(_fixture.DataSource, model);

        // The recipe's table, created by the owner; the app role only gets DML.
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.ticket_list
            (
                scope     text   NOT NULL,
                tenant_id text   NOT NULL,
                id        text   NOT NULL,
                title     text   NOT NULL,
                version   bigint NOT NULL,
                PRIMARY KEY (scope, tenant_id, id)
            );
            ALTER TABLE app.ticket_list ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.ticket_list FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS scope_isolation ON app.ticket_list;
            CREATE POLICY scope_isolation ON app.ticket_list
                USING (papuma.scope_visible(scope, tenant_id))
                WITH CHECK (papuma.scope_writable(scope, tenant_id));
            GRANT USAGE ON SCHEMA app TO {PostgresFixture.AppRoleName};
            GRANT SELECT, INSERT, UPDATE, DELETE ON app.ticket_list TO {PostgresFixture.AppRoleName};
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ScopeContext NewTenant() => ScopeContext.Tenant(Guid.NewGuid());

    [Fact]
    public async Task ScopeVisible_DecidesExactlyLikeTheKernelPolicy()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var ids = new List<string>();
        foreach (var scope in new[] { tenantA, tenantB, ScopeContext.Platform() })
        {
            await using var session = _store.OpenSession(scope);
            var id = Guid.NewGuid().ToString("N");
            await session.SaveAsync(new Ticket(id, "t"), 0);
            await session.CommitAsync();
            ids.Add(id);
        }

        foreach (var (setScope, expectedCount) in new (Func<NpgsqlConnection, NpgsqlTransaction, Task>, int)[]
        {
            ((c, t) => c.SetScopeAsync(t, tenantA), 1),
            ((c, t) => c.SetScopeAsync(t, ScopeContext.Platform()), 1),
            ((c, t) => c.SetAllScopesAsync(t), 3),
            ((_, _) => Task.CompletedTask, 0), // no scope at all
        })
        {
            // Kernel policy: what RLS lets the app role see.
            var byPolicy = await SelectIdsAsync(_fixture.AppRoleDataSource, setScope, ids,
                "SELECT id FROM papuma.document WHERE id = ANY(@ids)");

            // Function: evaluated by the superuser, whom RLS does not filter.
            var byFunction = await SelectIdsAsync(_fixture.DataSource, setScope, ids,
                "SELECT id FROM papuma.document WHERE id = ANY(@ids) AND papuma.scope_visible(scope, tenant_id)");

            Assert.Equal(expectedCount, byPolicy.Count);
            Assert.Equal(byPolicy.Order(), byFunction.Order());
        }
    }

    [Fact]
    public async Task ApplicationTable_ProjectedPerChangeScope_IsolatesLikeTheKernel()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var idA = await SaveTicketAsync(tenantA, "printer on fire");
        var idB = await SaveTicketAsync(tenantB, "coffee machine");

        var projection = new TicketListProjection(_store, _fixture.AppRoleDataSource, $"ticket-list-{Guid.NewGuid():N}");
        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [projection]);
        while (await processor.ProcessOnceAsync() > 0)
        {
        }

        var ids = new List<string> { idA, idB };
        const string select = "SELECT id FROM app.ticket_list WHERE id = ANY(@ids)";
        Assert.Equal(new[] { idA }, await SelectIdsAsync(_fixture.AppRoleDataSource, (c, t) => c.SetScopeAsync(t, tenantA), ids, select));
        Assert.Empty(await SelectIdsAsync(_fixture.AppRoleDataSource, (_, _) => Task.CompletedTask, ids, select));
        Assert.Equal(2, (await SelectIdsAsync(_fixture.AppRoleDataSource, (c, t) => c.SetAllScopesAsync(t), ids, select)).Count);

        await using (var session = _store.OpenSession(tenantB))
        {
            await session.DeleteAsync<Ticket>(idB, expectedVersion: 1);
            await session.CommitAsync();
        }

        while (await processor.ProcessOnceAsync() > 0)
        {
        }

        Assert.Equal(new[] { idA }, await SelectIdsAsync(_fixture.AppRoleDataSource, (c, t) => c.SetAllScopesAsync(t), ids, select));
    }

    [Fact]
    public async Task ApplicationTable_RejectsWritesOutsideTheScope_AndUnderAll()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await AssertInsertRejectedAsync((c, t) => c.SetScopeAsync(t, tenantA), tenantB);
        await AssertInsertRejectedAsync((c, t) => c.SetAllScopesAsync(t), tenantB);
        await AssertInsertRejectedAsync((_, _) => Task.CompletedTask, tenantB);
    }

    private async Task<string> SaveTicketAsync(ScopeContext scope, string title)
    {
        await using var session = _store.OpenSession(scope);
        var id = Guid.NewGuid().ToString("N");
        await session.SaveAsync(new Ticket(id, title), 0);
        await session.CommitAsync();
        return id;
    }

    private async Task AssertInsertRejectedAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> setScope, ScopeContext rowScope)
    {
        await using var conn = await _fixture.AppRoleDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await setScope(conn, tx);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO app.ticket_list VALUES (@scope, @tenantId, 'smuggled', 'x', 1)";
        cmd.Parameters.AddWithValue("scope", rowScope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", rowScope.TenantId ?? string.Empty);

        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState); // RLS WITH CHECK violation
    }

    private static async Task<List<string>> SelectIdsAsync(
        NpgsqlDataSource dataSource,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> setScope,
        IReadOnlyList<string> ids,
        string sql)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await setScope(conn, tx);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("ids", ids.ToArray());

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// The recipe's handler, verbatim apart from names: the diff triggers, the state is
    /// loaded in the change's scope, and the write runs under that same scope.
    /// </summary>
    private sealed class TicketListProjection(DocumentStore store, NpgsqlDataSource appData, string name)
        : IChangeHandler
    {
        public string Name => name;

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType != nameof(Ticket)) return;
            if (change.Operation != ChangeOperation.Delete && !change.FieldChanged("title")) return;

            DocumentResult<Ticket>? current = null;
            if (change.Operation != ChangeOperation.Delete)
            {
                await using var session = store.OpenSession(change.Scope);
                current = await session.LoadAsync<Ticket>(change.DocumentId, ct);
            }

            await using var conn = await appData.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.SetScopeAsync(tx, change.Scope, ct);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("scope", change.Scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", change.Scope.TenantId ?? string.Empty);
            cmd.Parameters.AddWithValue("id", change.DocumentId);

            if (current is null)
            {
                cmd.CommandText = """
                    DELETE FROM app.ticket_list
                    WHERE scope = @scope AND tenant_id = @tenantId AND id = @id
                    """;
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO app.ticket_list (scope, tenant_id, id, title, version)
                    VALUES (@scope, @tenantId, @id, @title, @version)
                    ON CONFLICT (scope, tenant_id, id) DO UPDATE
                        SET title = EXCLUDED.title, version = EXCLUDED.version
                        WHERE app.ticket_list.version < EXCLUDED.version
                    """;
                cmd.Parameters.AddWithValue("title", current.Document.Title);
                cmd.Parameters.AddWithValue("version", current.Version);
            }

            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
