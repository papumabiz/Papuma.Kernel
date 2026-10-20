// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// Purges events past their type-specific retention (ADR-013): the event log is a
/// fact store, not a version store — deleting per event type is legitimate. Retention
/// is opt-in per event type (<c>Event&lt;T&gt;(e =&gt; e.Retention(...))</c>); types
/// without retention are never touched.
/// </summary>
/// <remarks>
/// Retention periods should be much longer than consumer lag — purged events are gone
/// for late consumers and rebuilds alike. Run from a periodic job (phase 9 hosting).
/// The purge runs inside row-level security: it finds the affected rows under the
/// <c>All</c> scope (which reads, never writes) and deletes them under each tenant's scope.
/// It works in batches of 1000 events, each in its own transaction: a large backlog
/// (the first purge after retention was switched on) never holds one long lock or bloats
/// one transaction, and cancellation keeps everything deleted so far.
/// </remarks>
public static class EventRetention
{
    /// <summary>Events deleted per transaction.</summary>
    internal const int DefaultBatchSize = 1000;

    /// <summary>
    /// Deletes all events of retention-configured types that are older than their
    /// retention period. Returns the number of deleted events.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="model">The kernel model carrying the retention configuration.</param>
    /// <param name="ct">
    /// A cancellation token. Checked between the batches: events deleted by batches that
    /// already committed stay deleted.
    /// </param>
    public static Task<int> PurgeExpiredAsync(
        NpgsqlDataSource dataSource,
        KernelModel model,
        CancellationToken ct = default) =>
        PurgeExpiredAsync(dataSource, model, DefaultBatchSize, onBatchCommitted: null, ct);

    /// <summary>
    /// Same as the public overload, with a configurable batch size and a hook called
    /// after each committed batch (the tests' view into the batch boundaries).
    /// </summary>
    internal static async Task<int> PurgeExpiredAsync(
        NpgsqlDataSource dataSource,
        KernelModel model,
        int batchSize,
        Action<int>? onBatchCommitted,
        CancellationToken ct,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var deleted = 0;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var eventType in model.EventTypes.Where(e => e.Retention is not null))
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var batch = await PurgeBatchAsync(conn, eventType, batchSize, logger, ct);
                deleted += batch.Deleted;
                if (batch.Deleted > 0)
                {
                    onBatchCommitted?.Invoke(batch.Deleted);
                }

                // A short batch means the backlog is drained. Nothing deleted although rows
                // were found means they are not ours to delete (row-level security, a
                // concurrent purge) — stop instead of polling the same rows forever.
                if (batch.Selected < batchSize || batch.Deleted == 0)
                {
                    break;
                }
            }
        }

        return deleted;
    }

    private static async Task<(int Selected, int Deleted)> PurgeBatchAsync(
        NpgsqlConnection conn, EventTypeMetadata eventType, int batchSize, ILogger? logger, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(tx, ct);

        var seqsByScope = new Dictionary<(string Scope, string TenantId), List<long>>();
        var selected = 0;
        await using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT seq, scope, tenant_id FROM papuma.event
                WHERE event_type = @type AND occurred_at < now() - @retention
                ORDER BY occurred_at, seq
                LIMIT @batch
                """;
            select.Parameters.AddWithValue("type", eventType.Name);
            select.Parameters.AddWithValue("retention", eventType.Retention!.Value);
            select.Parameters.AddWithValue("batch", batchSize);

            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(1), reader.GetString(2));
                if (!seqsByScope.TryGetValue(key, out var seqs))
                {
                    seqsByScope[key] = seqs = [];
                }

                seqs.Add(reader.GetInt64(0));
                selected++;
            }
        }

        var deleted = 0;
        foreach (var ((scope, tenantId), seqs) in seqsByScope)
        {
            if (!ScopedMaintenance.TryFromRow(scope, tenantId, out var rowScope))
            {
                // Selected, never deleted: the batch ends once nothing else is deletable (see PurgeExpiredAsync).
                ScopedMaintenance.LogSkippedScope(logger, scope, tenantId, $"{seqs.Count} expired {eventType.Name} event(s)");
                continue;
            }

            await conn.SetScopeAsync(tx, rowScope, ct);

            await using var delete = conn.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM papuma.event
                WHERE scope = @scope AND tenant_id = @tenantId AND seq = ANY(@seqs)
                """;
            delete.Parameters.AddWithValue("scope", scope);
            delete.Parameters.AddWithValue("tenantId", tenantId);
            delete.Parameters.AddWithValue("seqs", seqs.ToArray());
            deleted += await delete.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (selected, deleted);
    }
}
