// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

[Collection(PostgresCollection.Name)]
public sealed class SchemaManagerTests
{
    private readonly PostgresFixture _fixture;

    public SchemaManagerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'papuma'
              AND table_name IN ('document', 'change', 'checkpoint', 'failure')
            """;

        var tableCount = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(4, tableCount);
    }

    [Fact]
    public async Task EnsureSchemaAsync_EnablesForcedRowLevelSecurity_OnTenantTables()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT relname
            FROM pg_class
            JOIN pg_namespace ON pg_namespace.oid = pg_class.relnamespace
            WHERE nspname = 'papuma'
              AND relname IN ('document', 'change')
              AND relrowsecurity
              AND relforcerowsecurity
            """;

        var secured = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            secured.Add(reader.GetString(0));
        }

        Assert.Equal(["change", "document"], secured.Order().ToArray());
    }

    [Fact]
    public void EnsureMinimumServerVersion_AcceptsPostgres18()
    {
        SchemaManager.EnsureMinimumServerVersion(180000);
        SchemaManager.EnsureMinimumServerVersion(190001);
    }

    [Fact]
    public void EnsureMinimumServerVersion_RejectsOlderServers()
    {
        var ex = Assert.Throws<PostgresVersionNotSupportedException>(
            () => SchemaManager.EnsureMinimumServerVersion(170004));

        Assert.Equal(170004, ex.ServerVersionNum);
        Assert.Contains("RETURNING OLD/NEW", ex.Message, StringComparison.Ordinal);
    }
}
