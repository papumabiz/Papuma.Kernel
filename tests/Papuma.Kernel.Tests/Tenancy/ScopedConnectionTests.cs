// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Tenancy;

/// <summary>
/// <c>OpenScopedAsync</c> (feedback F-21): the short form of "connection, transaction,
/// scope, commands bound to it" for application tables under RLS (ADR-019).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopedConnectionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public ScopedConnectionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await _fixture.Database.EnsureSchemaAsync(new Papuma.Kernel.Model.KernelModelBuilder().Build());

        // An application table as the same-database recipe builds it; the app role gets DML only.
        await using var cmd = _fixture.DataSource.CreateCommand($"""
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.scoped_note
            (
                scope     text NOT NULL,
                tenant_id text NOT NULL,
                id        text NOT NULL,
                body      text NOT NULL,
                PRIMARY KEY (scope, tenant_id, id)
            );
            ALTER TABLE app.scoped_note ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.scoped_note FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS scope_isolation ON app.scoped_note;
            CREATE POLICY scope_isolation ON app.scoped_note
                USING (papuma.scope_visible(scope, tenant_id))
                WITH CHECK (papuma.scope_writable(scope, tenant_id));
            DROP POLICY IF EXISTS scope_delete ON app.scoped_note;
            CREATE POLICY scope_delete ON app.scoped_note AS RESTRICTIVE FOR DELETE
                USING (papuma.scope_writable(scope, tenant_id));
            GRANT USAGE ON SCHEMA app TO {PostgresFixture.AppRoleName};
            GRANT SELECT, INSERT, UPDATE, DELETE ON app.scoped_note TO {PostgresFixture.AppRoleName};
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task InsertAsync(ScopeContext scope, string id, bool commit = true)
    {
        await using var scoped = await _fixture.AppRoleDataSource.OpenScopedAsync(scope);
        await using var cmd = scoped.CreateCommand(
            "INSERT INTO app.scoped_note (scope, tenant_id, id, body) VALUES (@scope, @tenant, @id, 'note')");
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenant", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
        if (commit)
        {
            await scoped.CommitAsync();
        }
    }

    private async Task<List<string>> VisibleIdsAsync(ScopeContext scope, IEnumerable<string> ids)
    {
        await using var scoped = await _fixture.AppRoleDataSource.OpenScopedAsync(scope);
        await using var cmd = scoped.CreateCommand("SELECT id FROM app.scoped_note WHERE id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        var visible = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            visible.Add(reader.GetString(0));
        }

        return visible;
    }

    [Fact]
    public async Task Reads_SeeOnlyTheirScope()
    {
        var tenantA = ScopeContext.Tenant(Guid.NewGuid());
        var tenantB = ScopeContext.Tenant(Guid.NewGuid());
        var idA = NewId();
        var idB = NewId();
        await InsertAsync(tenantA, idA);
        await InsertAsync(tenantB, idB);

        Assert.Equal([idA], await VisibleIdsAsync(tenantA, [idA, idB]));
        Assert.Equal([idB], await VisibleIdsAsync(tenantB, [idA, idB]));
    }

    [Fact]
    public async Task Dispose_WithoutCommit_RollsBack()
    {
        var tenant = ScopeContext.Tenant(Guid.NewGuid());
        var id = NewId();
        await InsertAsync(tenant, id, commit: false);

        Assert.Empty(await VisibleIdsAsync(tenant, [id]));
    }

    [Fact]
    public async Task Writes_ForAnotherScope_AreRejectedByThePolicy()
    {
        var tenantA = ScopeContext.Tenant(Guid.NewGuid());
        var tenantB = ScopeContext.Tenant(Guid.NewGuid());

        await using var scoped = await _fixture.AppRoleDataSource.OpenScopedAsync(tenantA);
        await using var cmd = scoped.CreateCommand(
            "INSERT INTO app.scoped_note (scope, tenant_id, id, body) VALUES ('Tenant', @tenant, @id, 'x')");
        cmd.Parameters.AddWithValue("tenant", tenantB.TenantId!);
        cmd.Parameters.AddWithValue("id", NewId());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState); // row-level security violation
    }

    [Fact]
    public async Task AllScope_DeletesNothing_InKernelAndApplicationTables()
    {
        var tenant = ScopeContext.Tenant(Guid.NewGuid());
        var noteId = NewId();
        await InsertAsync(tenant, noteId);

        await using var conn = await _fixture.AppRoleDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.SetAllScopesAsync(tx);
        foreach (var sql in new[]
        {
            "DELETE FROM app.scoped_note WHERE id = @id",   // the recipe's policy pair
            "DELETE FROM papuma.document",                  // the kernel's own tables
            "DELETE FROM papuma.change",
            "DELETE FROM papuma.event",
        })
        {
            await using var delete = conn.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = sql;
            delete.Parameters.AddWithValue("id", noteId);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync()); // All reads, never writes (ADR-019)
        }

        await tx.RollbackAsync();
        Assert.Equal([noteId], await VisibleIdsAsync(tenant, [noteId]));
    }

    [Fact]
    public async Task ProjectionReset_Truncate_ByTheAppRole_EmptiesEveryTenant()
    {
        await InsertAsync(ScopeContext.Tenant(Guid.NewGuid()), NewId());
        await InsertAsync(ScopeContext.Tenant(Guid.NewGuid()), NewId());

        // TRUNCATE is not subject to row-level security, so a projection's ResetAsync
        // empties every tenant's rows without a scope (ADR-024); the role needs the privilege.
        await using (var grant = _fixture.DataSource.CreateCommand(
            $"GRANT TRUNCATE ON app.scoped_note TO {PostgresFixture.AppRoleName}"))
        {
            await grant.ExecuteNonQueryAsync();
        }

        await using (var truncate = _fixture.AppRoleDataSource.CreateCommand("TRUNCATE app.scoped_note"))
        {
            await truncate.ExecuteNonQueryAsync();
        }

        await using var count = _fixture.DataSource.CreateCommand("SELECT count(*) FROM app.scoped_note");
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreateCommand_AfterCommit_Throws()
    {
        await using var scoped = await _fixture.AppRoleDataSource.OpenScopedAsync(ScopeContext.Tenant(Guid.NewGuid()));
        await scoped.CommitAsync();

        Assert.Throws<InvalidOperationException>(() => scoped.CreateCommand("SELECT 1"));
    }
}
