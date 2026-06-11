// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

namespace Papuma.Kernel.Store;

/// <summary>
/// Creates and maintains the kernel database schema.
/// </summary>
public static class SchemaManager
{
    /// <summary>
    /// The minimum supported <c>server_version_num</c> (PostgreSQL 18.0, ADR-001).
    /// </summary>
    public const int MinimumServerVersionNum = 180000;

    /// <summary>
    /// Verifies the server version and applies the idempotent kernel schema
    /// (tables, indexes, RLS policies). Safe to call repeatedly, e.g. on every startup.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="PostgresVersionNotSupportedException">
    /// Thrown when the connected server is older than PostgreSQL 18.
    /// </exception>
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.CommandText = "SELECT current_setting('server_version_num')::int";
            var serverVersionNum = (int)(await versionCmd.ExecuteScalarAsync(ct))!;
            EnsureMinimumServerVersion(serverVersionNum);
        }

        await using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = SchemaDdl.Script;
        await schemaCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Validates a reported <c>server_version_num</c> against the supported minimum.
    /// </summary>
    /// <param name="serverVersionNum">The reported server version number.</param>
    /// <exception cref="PostgresVersionNotSupportedException">
    /// Thrown when the version is below <see cref="MinimumServerVersionNum"/>.
    /// </exception>
    internal static void EnsureMinimumServerVersion(int serverVersionNum)
    {
        if (serverVersionNum < MinimumServerVersionNum)
        {
            throw new PostgresVersionNotSupportedException(serverVersionNum);
        }
    }
}
