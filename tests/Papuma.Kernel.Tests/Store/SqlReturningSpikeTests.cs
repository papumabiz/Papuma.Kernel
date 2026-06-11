// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Spike: verifies the riskiest design assumption (ADR-003) directly against PostgreSQL 18 —
/// a single UPDATE/DELETE statement returns both the pre- and post-change row state
/// via <c>RETURNING OLD/NEW</c>, atomically and without a prior read.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SqlReturningSpikeTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public SqlReturningSpikeTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Update_ReturnsOldAndNewState_InOneStatement()
    {
        var id = $"spike_{Guid.NewGuid():N}";
        await InsertAsync(id, """{"name": "Harry"}""");

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE papuma.document
            SET data = @data, version = version + 1, updated_at = now()
            WHERE scope = 'Platform' AND tenant_id = '' AND document_type = 'Spike' AND id = @id
              AND version = 1
            RETURNING old.data ->> 'name' AS old_name,
                      new.data ->> 'name' AS new_name,
                      new.version AS new_version
            """;
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = """{"name": "Harald"}""" });
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Harry", reader.GetString(0));
        Assert.Equal("Harald", reader.GetString(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Update_WithStaleVersion_AffectsZeroRows()
    {
        var id = $"spike_{Guid.NewGuid():N}";
        await InsertAsync(id, """{"name": "Harry"}""");

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE papuma.document
            SET data = @data, version = version + 1
            WHERE scope = 'Platform' AND tenant_id = '' AND document_type = 'Spike' AND id = @id
              AND version = 99
            RETURNING old.data, new.data
            """;
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = "{}" });
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Delete_ReturnsOldState()
    {
        var id = $"spike_{Guid.NewGuid():N}";
        await InsertAsync(id, """{"name": "Gone"}""");

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM papuma.document
            WHERE scope = 'Platform' AND tenant_id = '' AND document_type = 'Spike' AND id = @id
              AND version = 1
            RETURNING old.data ->> 'name' AS old_name
            """;
        cmd.Parameters.AddWithValue("id", id);

        var oldName = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("Gone", oldName);
    }

    private async Task InsertAsync(string id, string json)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO papuma.document (scope, tenant_id, document_type, id, version, schema_version, data)
            VALUES ('Platform', '', 'Spike', @id, 1, 1, @data)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = json });
        await cmd.ExecuteNonQueryAsync();
    }
}
