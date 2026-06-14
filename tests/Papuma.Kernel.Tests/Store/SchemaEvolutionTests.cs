// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using NpgsqlTypes;

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Integration tests for lazy upcasting and the schema version guards (ADR-005).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaEvolutionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public SchemaEvolutionTests(PostgresFixture fixture)
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

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
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

        // The rejected UPDATE must have rolled back: stored state is untouched.
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
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO papuma.document (scope, tenant_id, document_type, id, version, schema_version, data)
            VALUES (@scope, @tenantId, 'EvolvedDoc', @id, 1, @schemaVersion, @data)
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("schemaVersion", schemaVersion);
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = json.Replace("ID", id) });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> StoredSchemaVersionAsync(ScopeContext scope, string id)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_version FROM papuma.document
            WHERE scope = @scope AND tenant_id = @tenantId AND document_type = 'EvolvedDoc' AND id = @id
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", id);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
