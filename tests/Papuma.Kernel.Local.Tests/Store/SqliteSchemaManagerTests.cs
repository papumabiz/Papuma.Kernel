// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Tests for <see cref="SqliteSchemaManager"/> — mirrors
/// <c>Papuma.Kernel.Tests.Store.SchemaManagerTests</c> minus the two Postgres-only
/// concerns it also covers (RLS enablement, server-version checks) — neither exists for
/// a bundled, single-writer embedded engine, by design (see
/// docs/analyses/local-kernel-sqlite-sibling.md).
/// </summary>
public sealed class SqliteSchemaManagerTests
{
    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"papuma_schema_test_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            await SqliteSchemaManager.EnsureSchemaAsync(conn);
            await SqliteSchemaManager.EnsureSchemaAsync(conn);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT count(*) FROM sqlite_master
                WHERE type = 'table' AND name IN ('document', 'change', 'event', 'checkpoint', 'failure')
                """;
            var tableCount = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(5, tableCount);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_MaterializesDeclaredKeyIndexes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"papuma_schema_test_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            var model = new KernelModelBuilder()
                .Document<KeyedDoc>(d => d.UniqueKey(x => x.Email))
                .Build();
            await SqliteSchemaManager.EnsureSchemaAsync(conn, model);
            // Idempotent even with a model supplied.
            await SqliteSchemaManager.EnsureSchemaAsync(conn, model);

            var expectedIndexName = model.DocumentTypes.Single().Keys.Single().IndexName;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = @name";
            cmd.Parameters.AddWithValue("name", expectedIndexName);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    private sealed record KeyedDoc(string Id, string Email);
}
