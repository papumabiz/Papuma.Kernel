// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text;

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

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
    /// (tables, indexes, RLS policies). When a <paramref name="model"/> is supplied,
    /// declared keys are materialized as partial expression indexes (ADR-006).
    /// Safe to call repeatedly, e.g. on every startup.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="model">The kernel model whose declared keys are materialized (optional).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="PostgresVersionNotSupportedException">
    /// Thrown when the connected server is older than PostgreSQL 18.
    /// </exception>
    public static async Task EnsureSchemaAsync(
        NpgsqlDataSource dataSource,
        KernelModel? model = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.CommandText = "SELECT current_setting('server_version_num')::int";
            var serverVersionNum = (int)(await versionCmd.ExecuteScalarAsync(ct))!;
            EnsureMinimumServerVersion(serverVersionNum);
        }

        await using (var schemaCmd = conn.CreateCommand())
        {
            schemaCmd.CommandText = SchemaDdl.Script;
            await schemaCmd.ExecuteNonQueryAsync(ct);
        }

        if (model is not null)
        {
            var keyDdl = BuildKeyIndexDdl(model);
            if (keyDdl.Length > 0)
            {
                await using var keyCmd = conn.CreateCommand();
                keyCmd.CommandText = keyDdl;
                await keyCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>
    /// Convenience overload without a model.
    /// </summary>
    public static Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        EnsureSchemaAsync(dataSource, model: null, ct);

    /// <summary>
    /// Generates idempotent DDL for all declared key indexes. Identifiers are built
    /// from validated document type names and property-derived path segments — no
    /// user-supplied free text reaches the DDL.
    /// </summary>
    internal static string BuildKeyIndexDdl(KernelModel model)
    {
        var ddl = new StringBuilder();
        foreach (var metadata in model.DocumentTypes.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            foreach (var key in metadata.Keys)
            {
                foreach (var segment in key.PathSegments)
                {
                    InputValidator.ValidateDocumentType(segment); // same identifier pattern as type names
                }

                var pathLiteral = string.Join(',', key.PathSegments);
                var unique = key.Unique ? "UNIQUE " : string.Empty;
                ddl.AppendLine($"CREATE {unique}INDEX IF NOT EXISTS {key.IndexName}");
                ddl.AppendLine($"    ON papuma.document (scope, tenant_id, (data #>> '{{{pathLiteral}}}'))");
                ddl.AppendLine($"    WHERE document_type = '{metadata.Name}';");
            }
        }

        return ddl.ToString();
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
