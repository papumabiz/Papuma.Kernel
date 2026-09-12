// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Creates and maintains the SQLite kernel database schema — the
/// <c>Papuma.Kernel.Store.SchemaManager</c> (Postgres) counterpart for
/// <c>Papuma.Kernel.Local</c>. No server-version check: unlike a Postgres server,
/// the SQLite engine version is bundled with the NuGet package, not a deployment variable.
/// </summary>
public static class SqliteSchemaManager
{
    /// <summary>
    /// Applies the idempotent kernel schema (tables, indexes). When a
    /// <paramref name="model"/> is supplied, declared keys are materialized as partial
    /// expression indexes (ADR-006). Safe to call repeatedly, e.g. on every startup.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="model">The kernel model whose declared keys are materialized (optional).</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        KernelModel? model = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using (var schemaCmd = connection.CreateCommand())
        {
            schemaCmd.CommandText = SqliteSchemaDdl.Script;
            await schemaCmd.ExecuteNonQueryAsync(ct);
        }

        if (model is not null)
        {
            var keyDdl = BuildKeyIndexDdl(model);
            if (keyDdl.Length > 0)
            {
                await using var keyCmd = connection.CreateCommand();
                keyCmd.CommandText = keyDdl;
                await keyCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>
    /// Generates idempotent DDL for all declared key indexes, using SQLite's
    /// <c>json_extract</c> instead of Postgres's <c>#&gt;&gt;</c> path-extraction operator.
    /// Identifiers are built from validated document type names and property-derived path
    /// segments — no user-supplied free text reaches the DDL.
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

                var jsonPath = "$." + string.Join('.', key.PathSegments);
                var unique = key.Unique ? "UNIQUE " : string.Empty;
                ddl.AppendLine($"CREATE {unique}INDEX IF NOT EXISTS {key.IndexName}");
                ddl.AppendLine($"    ON document (scope, tenant_id, json_extract(data, '{jsonPath}'))");
                ddl.AppendLine($"    WHERE document_type = '{metadata.Name}';");
            }
        }

        return ddl.ToString();
    }
}
