// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Changes;
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
    /// Safe to call repeatedly, e.g. on every startup, and from several instances at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starts are serialized by a transaction-scoped advisory lock, and the DDL runs only when it is
    /// new to the database: the kernel schema script and the key indexes of a model are recorded in
    /// <c>papuma.schema_state</c> by a content hash, so an unchanged restart takes no <c>ACCESS
    /// EXCLUSIVE</c> lock on the kernel tables (review S-11, N-8). A different model, or a newer
    /// kernel version, hashes differently and applies its DDL. The kernel's own objects are not
    /// re-created behind your back: to force a re-apply (e.g. after dropping an index by hand),
    /// delete the rows of <c>papuma.schema_state</c>.
    /// </para>
    /// </remarks>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="model">The kernel model whose declared keys are materialized (optional).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="PostgresVersionNotSupportedException">
    /// Thrown when the connected server is older than PostgreSQL 18.
    /// </exception>
    public static Task EnsureSchemaAsync(
        NpgsqlDataSource dataSource,
        KernelModel? model = null,
        CancellationToken ct = default) =>
        EnsureSchemaAsync(dataSource, model, logger: null, ct);

    internal static async Task EnsureSchemaAsync(
        NpgsqlDataSource dataSource,
        KernelModel? model,
        ILogger? logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        // Built (and validated) before the database is touched: a bad model fails without a lock or any DDL.
        var keyDdl = model is null ? string.Empty : BuildKeyIndexDdl(model);

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.CommandText = "SELECT current_setting('server_version_num')::int";
            var serverVersionNum = (int)(await versionCmd.ExecuteScalarAsync(ct))!;
            EnsureMinimumServerVersion(serverVersionNum);
        }

        await ApplySchemaOnceAsync(conn, keyDdl, ct);
        await RepairFeedAfterLogicalRestoreAsync(conn, logger, ct);
    }

    private const string KernelComponent = "kernel";

    private const string KeysComponent = "keys";

    /// <summary>
    /// Applies the kernel script and the key DDL under the schema lock, each only when its content
    /// hash is not yet recorded in <c>papuma.schema_state</c> (one row per applied hash, so several
    /// models sharing a database never mask one another).
    /// </summary>
    private static async Task ApplySchemaOnceAsync(NpgsqlConnection conn, string keyDdl, CancellationToken ct)
    {
        var kernelHash = ContentHash(SchemaDdl.Script);
        var keysHash = keyDdl.Length == 0 ? null : ContentHash(keyDdl);

        await using var tx = await conn.BeginTransactionAsync(ct);

        // Released with the transaction. Blocks parallel starts until the first one has applied and recorded
        // its DDL; they then find it recorded and skip. Contributors take their own lock (ISchemaContributor).
        await ExecuteAsync(conn, tx, "SELECT pg_advisory_xact_lock(hashtextextended('papuma.kernel.schema', 0))", ct);

        var applied = await ReadAppliedAsync(conn, tx, ct);
        var kernelDone = applied.Contains((KernelComponent, kernelHash));
        var keysDone = keysHash is null || applied.Contains((KeysComponent, keysHash));
        if (kernelDone && keysDone)
        {
            await tx.CommitAsync(ct); // nothing new: no DDL, so no ACCESS EXCLUSIVE lock
            return;
        }

        // A database from before schema_state gets the table with the script, and is recorded below.
        if (!kernelDone)
        {
            await ExecuteAsync(conn, tx, SchemaDdl.Script, ct);
            await RecordAsync(conn, tx, KernelComponent, kernelHash, ct);
        }

        if (!keysDone)
        {
            await ExecuteAsync(conn, tx, keyDdl, ct);
            await RecordAsync(conn, tx, KeysComponent, keysHash!, ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<HashSet<(string Component, string Hash)>> ReadAppliedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var applied = new HashSet<(string, string)>();

        await using var exists = new NpgsqlCommand("SELECT to_regclass('papuma.schema_state') IS NOT NULL", conn, tx);
        if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
        {
            return applied;
        }

        await using var read = new NpgsqlCommand("SELECT component, content_hash FROM papuma.schema_state", conn, tx);
        await using var reader = await read.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied.Add((reader.GetString(0), reader.GetString(1)));
        }

        return applied;
    }

    private static async Task RecordAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string component, string hash, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO papuma.schema_state (component, content_hash) VALUES (@component, @hash)
            ON CONFLICT DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("component", component);
        cmd.Parameters.AddWithValue("hash", hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Line endings are normalized: the script is a raw string literal, which carries the source file's.
    private static string ContentHash(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

    /// <summary>
    /// Records the hash key in use and verifies it against the key the database was initialized
    /// with (ADR-025, point 5). The first instance to start with a key id stores its check value;
    /// every later instance with the same id must compute the same value. Call it once at startup
    /// after <see cref="EnsureSchemaAsync(NpgsqlDataSource, KernelModel?, CancellationToken)"/>; the
    /// hosting bootstrap does so when a key is configured.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="hashKeys">The key provider this instance writes hashes with.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="HashKeyMismatchException">The configured key differs from the stored one for the same key id.</exception>
    /// <exception cref="InvalidOperationException">The <c>papuma.hash_key_check</c> table does not exist; run <c>EnsureSchemaAsync</c>.</exception>
    public static async Task EnsureHashKeyAsync(
        NpgsqlDataSource dataSource,
        IHashKeyProvider hashKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(hashKeys);

        var keyId = hashKeys.ActiveKeyId;
        var computed = HashKeyCheckValue.Compute(hashKeys);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        string? stored;
        try
        {
            // Insert-or-ignore, then read: concurrent first starts all compare with the winner.
            await using (var insert = conn.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO papuma.hash_key_check (kid, check_value) VALUES (@kid, @value)
                    ON CONFLICT (kid) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("kid", keyId);
                insert.Parameters.AddWithValue("value", computed);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await using var select = conn.CreateCommand();
            select.CommandText = "SELECT check_value FROM papuma.hash_key_check WHERE kid = @kid";
            select.Parameters.AddWithValue("kid", keyId);
            stored = (string?)await select.ExecuteScalarAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidOperationException(
                "The table papuma.hash_key_check does not exist. Run SchemaManager.EnsureSchemaAsync " +
                "(PapumaKernelOptions.EnsureSchema) once with this version before starting with a hash key.", ex);
        }

        HashKeyCheckValue.Verify(keyId, computed, stored ?? throw new InvalidOperationException(
            $"The check value for hash key id '{keyId}' is neither stored nor readable in papuma.hash_key_check " +
            "(concurrently removed, or hidden from this database role)."));
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
        return await RepairFeedAfterLogicalRestoreAsync(conn, logger: null, ct);
    }

    private static async Task<bool> RepairFeedAfterLogicalRestoreAsync(
        NpgsqlConnection conn, ILogger? logger, CancellationToken ct)
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
                """, bind: null, ct, logger);
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
