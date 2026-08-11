// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Integration tests for lazy upcasting and the schema version guards — mirrors
/// <c>Papuma.Kernel.Tests.Store.SchemaEvolutionTests</c>. The upcaster chain itself
/// (<c>DocumentTypeMetadata.Upcast</c>) is Core code, already exercised by the Postgres
/// suite — these tests prove the SQLite write/read path calls it correctly.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteSchemaEvolutionTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteSchemaEvolutionTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Current class shape (schema v3). Historic shapes:
    /// v1 used "mail" instead of "email"; v2 used a flat "city" instead of "address.city".
    /// </summary>
    private sealed record EvolvedDoc(string Id, string Email, EvolvedAddress? Address = null);

    private sealed record EvolvedAddress(string City);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<EvolvedDoc>(d => d
                .Upcast(1, json =>
                {
                    json["email"] = json["mail"]?.DeepClone();
                    json.Remove("mail");
                })
                .Upcast(2, json =>
                {
                    if (json.Remove("city", out var city))
                    {
                        json["address"] = new System.Text.Json.Nodes.JsonObject { ["city"] = city };
                    }
                }))
            .Build();

        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task Load_RunsUpcasterChain_AcrossMultipleVersions()
    {
        var scope = NewTenant();
        var id = NewId();
        // Seed a v1 document: "mail" instead of "email", flat "city".
        await SeedRawDocumentAsync(scope, id, """{"id": "ID", "mail": "h@x.de", "city": "Bonn"}""", schemaVersion: 1);

        await using var session = _store.OpenSession(scope);
        var loaded = await session.LoadAsync<EvolvedDoc>(id);

        Assert.NotNull(loaded);
        Assert.Equal("h@x.de", loaded.Document.Email);
        Assert.Equal("Bonn", loaded.Document.Address?.City);
    }

    [Fact]
    public async Task Load_IsLazy_StoredRowKeepsOldSchemaVersion()
    {
        var scope = NewTenant();
        var id = NewId();
        await SeedRawDocumentAsync(scope, id, """{"id": "ID", "mail": "h@x.de"}""", schemaVersion: 1);

        await using var session = _store.OpenSession(scope);
        _ = await session.LoadAsync<EvolvedDoc>(id);

        Assert.Equal(1, await StoredSchemaVersionAsync(scope, id));
    }

    [Fact]
    public async Task Save_PersistsTheLiftedState_WithCurrentSchemaVersion()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var id = NewId();
        await SeedRawDocumentAsync(scope, id, """{"id": "ID", "mail": "h@x.de"}""", schemaVersion: 1);

        var loaded = await session.LoadAsync<EvolvedDoc>(id);
        await session.SaveAsync(loaded!.Document, loaded.Version);
        await session.CommitAsync();

        Assert.Equal(3, await StoredSchemaVersionAsync(scope, id));
        var reloaded = await session.LoadAsync<EvolvedDoc>(id);
        Assert.Equal("h@x.de", reloaded!.Document.Email);
    }

    [Fact]
    public async Task Load_Throws_WhenStoredSchemaIsNewerThanModel()
    {
        var scope = NewTenant();
        var id = NewId();
        await SeedRawDocumentAsync(scope, id, """{"id": "ID", "email": "h@x.de"}""", schemaVersion: 99);

        await using var session = _store.OpenSession(scope);
        var ex = await Assert.ThrowsAsync<SchemaVersionConflictException>(
            () => session.LoadAsync<EvolvedDoc>(id));

        Assert.Equal(99, ex.StoredSchemaVersion);
        Assert.Equal(3, ex.ModelSchemaVersion);
    }

    [Fact]
    public async Task Save_Throws_AndRollsBack_WhenStoredSchemaIsNewerThanModel()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var id = NewId();
        await SeedRawDocumentAsync(scope, id, """{"id": "ID", "email": "keep@x.de"}""", schemaVersion: 99);

        await Assert.ThrowsAsync<SchemaVersionConflictException>(
            () => session.SaveAsync(new EvolvedDoc(id, "overwrite@x.de"), expectedVersion: 1));

        // The rejected write must not have persisted: stored state is untouched. Unlike
        // Postgres (where the UPDATE physically ran before the check threw and needed a
        // savepoint rollback), the SQLite session checks the schema version *before*
        // issuing the UPDATE (see UpdateAsync) — no partial write ever happens here.
        Assert.Equal(99, await StoredSchemaVersionAsync(scope, id));
    }

    [Fact]
    public async Task Delete_Throws_WhenStoredSchemaIsNewerThanModel()
    {
        var scope = NewTenant();
        var id = NewId();
        await SeedRawDocumentAsync(scope, id, """{"id": "ID", "email": "h@x.de"}""", schemaVersion: 99);

        await using var session = _store.OpenSession(scope);
        await Assert.ThrowsAsync<SchemaVersionConflictException>(
            () => session.DeleteAsync<EvolvedDoc>(id, expectedVersion: 1));

        Assert.Equal(99, await StoredSchemaVersionAsync(scope, id));
    }

    private async Task SeedRawDocumentAsync(ScopeContext scope, string id, string json, int schemaVersion)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document (scope, tenant_id, document_type, id, version, schema_version, data, created_at, updated_at)
            VALUES (@scope, @tenantId, 'EvolvedDoc', @id, 1, @schemaVersion, @data, @now, @now)
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("schemaVersion", schemaVersion);
        cmd.Parameters.AddWithValue("data", json.Replace("ID", id));
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> StoredSchemaVersionAsync(ScopeContext scope, string id)
    {
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_version FROM document
            WHERE scope = @scope AND tenant_id = @tenantId AND document_type = 'EvolvedDoc' AND id = @id
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", id);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }
}
