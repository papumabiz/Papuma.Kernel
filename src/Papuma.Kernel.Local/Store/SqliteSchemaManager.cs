// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Changes;
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

        // Files created before a column existed: SQLite has no ADD COLUMN IF NOT EXISTS.
        // projection_version: ADR-024. reset_count: the generation of the checkpoint, bumped by every
        // reset, so a batch can tell "reset to 0" from "never moved" (review L-3).
        await AddCheckpointColumnIfMissingAsync(connection, "projection_version", "INTEGER", ct);
        await AddCheckpointColumnIfMissingAsync(connection, "reset_count", "INTEGER NOT NULL DEFAULT 0", ct);

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

    // column and definition are constants of this class, never caller input.
    private static async Task AddCheckpointColumnIfMissingAsync(
        SqliteConnection connection, string column, string definition, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('checkpoint') WHERE name = @column";
        cmd.Parameters.AddWithValue("column", column);
        if ((long)(await cmd.ExecuteScalarAsync(ct))! != 0)
        {
            return;
        }

        cmd.Parameters.Clear();
        cmd.CommandText = $"ALTER TABLE checkpoint ADD COLUMN {column} {definition}";
        await cmd.ExecuteNonQueryAsync(ct);
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
                // Everything below is interpolated into DDL text, so each part is validated here, where it is
                // used — not only when the model was built (review S-12, L-18).
                InputValidator.ValidateDocumentType(metadata.Name);
                foreach (var segment in key.ComponentSegments.SelectMany(s => s))
                {
                    InputValidator.ValidateJsonPathSegment(segment); // first: the message that names the property rule
                }

                InputValidator.ValidateIndexName(key.IndexName);

                // One expression per component, in declaration order (composite keys, ADR-020).
                var columns = string.Join(", ", key.ComponentSegments
                    .Select(segments => $"json_extract(data, '$.{string.Join('.', segments)}')"));
                var unique = key.Unique ? "UNIQUE " : string.Empty;
                ddl.AppendLine(CultureInfo.InvariantCulture, $"CREATE {unique}INDEX IF NOT EXISTS {key.IndexName}");
                ddl.AppendLine(CultureInfo.InvariantCulture, $"    ON document (scope, tenant_id, {columns})");
                ddl.AppendLine(CultureInfo.InvariantCulture, $"    WHERE document_type = '{metadata.Name}';");
            }
        }

        return ddl.ToString();
    }

    /// <summary>
    /// Records the hash key in use and verifies it against the key the database file was
    /// initialized with (ADR-025, point 5) — the counterpart of
    /// <c>SchemaManager.EnsureHashKeyAsync</c>. The first start with a key id stores its check value;
    /// every later start with the same id must compute the same value. Call it once at startup after
    /// <see cref="EnsureSchemaAsync(SqliteConnection, KernelModel?, CancellationToken)"/>; the hosting
    /// bootstrap does so when a key is configured.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="hashKeys">The key provider this process writes hashes with.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="HashKeyMismatchException">The configured key differs from the stored one for the same key id.</exception>
    /// <exception cref="InvalidOperationException">The <c>hash_key_check</c> table does not exist; run <c>EnsureSchemaAsync</c>.</exception>
    public static async Task EnsureHashKeyAsync(
        SqliteConnection connection,
        IHashKeyProvider hashKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(hashKeys);

        var keyId = hashKeys.ActiveKeyId;
        var computed = HashKeyCheckValue.Compute(hashKeys);

        string? stored;
        try
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO hash_key_check (kid, check_value, created_at) VALUES (@kid, @value, @now)
                    ON CONFLICT (kid) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("@kid", keyId);
                insert.Parameters.AddWithValue("@value", computed);
                insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync(ct);
            }

            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT check_value FROM hash_key_check WHERE kid = @kid";
            select.Parameters.AddWithValue("@kid", keyId);
            stored = (string?)await select.ExecuteScalarAsync(ct);
        }
        catch (SqliteException ex) when (IsMissingTable(ex))
        {
            throw new InvalidOperationException(
                "The table hash_key_check does not exist. Run SqliteSchemaManager.EnsureSchemaAsync " +
                "(PapumaKernelLocalOptions.EnsureSchema) once with this version before starting with a hash key.", ex);
        }

        HashKeyCheckValue.Verify(keyId, computed, stored!);
    }

    // "no such table" has no result code of its own: it is the generic SQLITE_ERROR (1), shared with every
    // syntax and binding error, so the code narrows the message check down instead of replacing it (review L-23).
    private const int SqliteError = 1;

    internal static bool IsMissingTable(SqliteException exception) =>
        exception.SqliteErrorCode == SqliteError
        && exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);
}
