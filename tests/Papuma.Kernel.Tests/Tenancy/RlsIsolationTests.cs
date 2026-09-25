// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Tenancy;

/// <summary>
/// Verifies layer 2 of the scope model (RLS) against a non-superuser role:
/// even without explicit WHERE predicates, tenants can only see their own rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RlsIsolationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public RlsIsolationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);
        await _fixture.GrantAppRoleAccessAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TenantScope_SeesOnlyOwnRows_EvenWithoutWherePredicate()
    {
        await InsertDocumentAsync(ScopeContext.Tenant("rls_tenant_a"), "doc_a");
        await InsertDocumentAsync(ScopeContext.Tenant("rls_tenant_b"), "doc_b");
        await InsertDocumentAsync(ScopeContext.Platform(), "doc_platform");

        var visibleToA = await SelectAllIdsAsync(ScopeContext.Tenant("rls_tenant_a"));

        Assert.Contains("doc_a", visibleToA);
        Assert.DoesNotContain("doc_b", visibleToA);
        Assert.DoesNotContain("doc_platform", visibleToA);
    }

    [Fact]
    public async Task GuidTenantScope_IsolatesLikeAnyOtherTenant()
    {
        var tenantA = ScopeContext.Tenant(Guid.NewGuid());
        var tenantB = ScopeContext.Tenant(Guid.NewGuid());
        await InsertDocumentAsync(tenantA, "doc_guid_a");
        await InsertDocumentAsync(tenantB, "doc_guid_b");

        var visibleToA = await SelectAllIdsAsync(tenantA);

        Assert.Contains("doc_guid_a", visibleToA);
        Assert.DoesNotContain("doc_guid_b", visibleToA);
    }

    [Fact]
    public async Task PlatformScope_SeesOnlyPlatformRows()
    {
        await InsertDocumentAsync(ScopeContext.Tenant("rls_tenant_c"), "doc_c");
        await InsertDocumentAsync(ScopeContext.Platform(), "doc_platform_2");

        var visible = await SelectAllIdsAsync(ScopeContext.Platform());

        Assert.Contains("doc_platform_2", visible);
        Assert.DoesNotContain("doc_c", visible);
    }

    [Fact]
    public async Task AllScope_SeesEverything()
    {
        await InsertDocumentAsync(ScopeContext.Tenant("rls_tenant_d"), "doc_d");
        await InsertDocumentAsync(ScopeContext.Platform(), "doc_platform_3");

        await using var conn = await _fixture.AppRoleDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.SetAllScopesAsync(tx);

        var visible = await SelectAllIdsAsync(conn);

        Assert.Contains("doc_d", visible);
        Assert.Contains("doc_platform_3", visible);
    }

    [Fact]
    public async Task MissingScope_SeesNothing()
    {
        await InsertDocumentAsync(ScopeContext.Tenant("rls_tenant_e"), "doc_e");

        await using var conn = await _fixture.AppRoleDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Deliberately no SetScopeAsync: a connection without scope must see zero rows.

        var visible = await SelectAllIdsAsync(conn);

        Assert.Empty(visible);
    }

    [Fact]
    public async Task WithCheck_RejectsWritesOutsideOwnScope()
    {
        await using var conn = await _fixture.AppRoleDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.SetScopeAsync(tx, ScopeContext.Tenant("rls_tenant_f"));

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO papuma.document (scope, tenant_id, document_type, id, version, schema_version, data)
            VALUES ('Tenant', 'rls_tenant_other', 'TestDoc', 'doc_smuggled', 1, 1, '{}'::jsonb)
            """;

        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    private async Task InsertDocumentAsync(ScopeContext scope, string documentId)
    {
        await using var conn = await _fixture.AppRoleDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.SetScopeAsync(tx, scope);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO papuma.document (scope, tenant_id, document_type, id, version, schema_version, data)
            VALUES (@scope, @tenantId, 'TestDoc', @id, 1, 1, '{}'::jsonb)
            ON CONFLICT DO NOTHING
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", documentId);

        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    private async Task<IReadOnlyList<string>> SelectAllIdsAsync(ScopeContext scope)
    {
        await using var conn = await _fixture.AppRoleDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.SetScopeAsync(tx, scope);

        return await SelectAllIdsAsync(conn);
    }

    private static async Task<IReadOnlyList<string>> SelectAllIdsAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        // Deliberately no WHERE clause: layer 1 is absent, only RLS (layer 2) filters.
        cmd.CommandText = "SELECT id FROM papuma.document";

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
