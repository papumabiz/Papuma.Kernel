// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;
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

        await RepairFeedAfterLogicalRestoreAsync(conn, ct);
    }

    /// <summary>
    /// Detects and repairs the feed state after a logical restore (<c>pg_dump</c> /
    /// <c>pg_restore</c>) into another cluster (ADR-022, point 7). Transaction ids belong to
    /// a cluster: restored <c>txid</c>s and stored cursor snapshots can lie beyond the new
    /// cluster's id counter — then stored snapshots would declare new transactions
    /// "already delivered" and restored rows would never become visible. Runs as part of
    /// <see cref="EnsureSchemaAsync(NpgsqlDataSource, KernelModel?, CancellationToken)"/>;
    /// call it directly when the schema is managed elsewhere. Physical backups, PITR and
    /// <c>pg_upgrade</c> keep the id space and never trigger it.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>true</c> when a restored state was found and repaired.</returns>
    public static async Task<bool> RepairFeedAfterLogicalRestoreAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await RepairFeedAfterLogicalRestoreAsync(conn, ct);
    }

    private static async Task<bool> RepairFeedAfterLogicalRestoreAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(tx, ct); // FORCE RLS: without it the owner sees no rows

        await using (var detect = conn.CreateCommand())
        {
            // An id at or beyond the next one to be assigned cannot exist within one cluster.
            detect.Transaction = tx;
            detect.CommandText = """
                SELECT EXISTS (SELECT 1 FROM papuma.change WHERE txid >= pg_snapshot_xmax(pg_current_snapshot()))
                    OR EXISTS (SELECT 1 FROM papuma.event WHERE txid >= pg_snapshot_xmax(pg_current_snapshot()))
                    OR EXISTS (SELECT 1 FROM papuma.checkpoint
                               WHERE pg_snapshot_xmax(done_snapshot) > pg_snapshot_xmax(pg_current_snapshot())
                                  OR pg_snapshot_xmax(slice_snapshot) > pg_snapshot_xmax(pg_current_snapshot()))
                """;
            if (!(bool)(await detect.ExecuteScalarAsync(ct))!)
            {
                await tx.CommitAsync(ct);
                return false;
            }
        }

        await using (var repair = conn.CreateCommand())
        {
            // 1. Each cursor falls back to a seq floor: just below its lowest undelivered row.
            //    Old txids and old snapshots are consistent with each other, so "undelivered"
            //    is still exact; rows above the floor that were delivered come again
            //    (at-least-once).
            repair.Transaction = tx;
            repair.CommandText = """
                UPDATE papuma.checkpoint cp
                SET base_seq = COALESCE(
                        (SELECT min(r.seq) - 1 FROM papuma.change r
                         WHERE (CASE WHEN cp.done_snapshot IS NULL THEN r.seq > cp.base_seq
                                     ELSE NOT pg_visible_in_snapshot(r.txid, cp.done_snapshot) END)
                           AND NOT (cp.slice_snapshot IS NOT NULL
                                    AND pg_visible_in_snapshot(r.txid, cp.slice_snapshot)
                                    AND r.seq <= cp.slice_seq)),
                        (SELECT COALESCE(max(seq), 0) FROM papuma.change)),
                    done_snapshot = NULL, slice_snapshot = NULL, slice_seq = 0, updated_at = now()
                WHERE cp.handler_name NOT LIKE 'event:%';

                UPDATE papuma.checkpoint cp
                SET base_seq = COALESCE(
                        (SELECT min(r.seq) - 1 FROM papuma.event r
                         WHERE (CASE WHEN cp.done_snapshot IS NULL THEN r.seq > cp.base_seq
                                     ELSE NOT pg_visible_in_snapshot(r.txid, cp.done_snapshot) END)
                           AND NOT (cp.slice_snapshot IS NOT NULL
                                    AND pg_visible_in_snapshot(r.txid, cp.slice_snapshot)
                                    AND r.seq <= cp.slice_seq)),
                        (SELECT COALESCE(max(seq), 0) FROM papuma.event)),
                    done_snapshot = NULL, slice_snapshot = NULL, slice_seq = 0, updated_at = now()
                WHERE cp.handler_name LIKE 'event:%';
                """;
            await repair.ExecuteNonQueryAsync(ct);
        }

        // 2. Restored txids become the frozen id, visible to every snapshot — every restored
        //    row is committed. Written tenant by tenant: the All scope reads, never writes.
        foreach (var table in new[] { "papuma.change", "papuma.event" })
        {
            var scopes = await ScopedMaintenance.ReadScopesAsync(conn, tx, $"""
                SELECT DISTINCT scope, tenant_id FROM {table}
                WHERE txid >= pg_snapshot_xmax(pg_current_snapshot())
                """, bind: null, ct);
            await ScopedMaintenance.ExecutePerScopeAsync(conn, tx, scopes, $"""
                UPDATE {table} SET txid = '2'::xid8
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND txid >= pg_snapshot_xmax(pg_current_snapshot())
                """, bind: null, ct);
        }

        await tx.CommitAsync(ct);
        return true;
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
                foreach (var segment in key.ComponentSegments.SelectMany(s => s))
                {
                    InputValidator.ValidateDocumentType(segment); // same identifier pattern as type names
                }

                // One expression per component, in declaration order (composite keys, ADR-020).
                var columns = string.Join(", ", key.ComponentSegments
                    .Select(segments => $"(data #>> '{{{string.Join(',', segments)}}}')"));
                var unique = key.Unique ? "UNIQUE " : string.Empty;
                ddl.AppendLine($"CREATE {unique}INDEX IF NOT EXISTS {key.IndexName}");
                ddl.AppendLine($"    ON papuma.document (scope, tenant_id, {columns})");
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
