// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Events;

/// <summary>
/// Purges events past their type-specific retention — SQLite counterpart of
/// <c>EventRetention</c>. Retention is opt-in per event type
/// (<c>Event&lt;T&gt;(e =&gt; e.Retention(...))</c>); types without retention are never
/// touched.
/// </summary>
/// <remarks>
/// Deletes in batches of 1000 events, each its own implicit transaction: a large backlog
/// never holds the write lock for the whole purge (a desktop UI writing meanwhile waits
/// for one batch, not for all of them), and cancellation keeps everything deleted so far.
/// </remarks>
public static class SqliteEventRetention
{
    /// <summary>Events deleted per transaction.</summary>
    internal const int DefaultBatchSize = 1000;

    /// <summary>
    /// Deletes all events of retention-configured types that are older than their
    /// retention period. Returns the number of deleted events.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="model">The kernel model carrying the retention configuration.</param>
    /// <param name="ct">
    /// A cancellation token. Checked between the batches: events deleted by batches that
    /// already committed stay deleted.
    /// </param>
    public static Task<int> PurgeExpiredAsync(
        string connectionString,
        KernelModel model,
        CancellationToken ct = default) =>
        PurgeExpiredAsync(connectionString, model, DefaultBatchSize, onBatchCommitted: null, ct);

    /// <summary>
    /// Same as the public overload, with a configurable batch size and a hook called
    /// after each committed batch (the tests' view into the batch boundaries).
    /// </summary>
    internal static async Task<int> PurgeExpiredAsync(
        string connectionString,
        KernelModel model,
        int batchSize,
        Action<int>? onBatchCommitted,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var deleted = 0;
        await using var conn = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
        foreach (var eventType in model.EventTypes.Where(e => e.Retention is not null))
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM event WHERE seq IN (
                        SELECT seq FROM event
                        WHERE event_type = @type AND occurred_at < @cutoff
                        ORDER BY seq
                        LIMIT @batch)
                    """;
                cmd.Parameters.AddWithValue("type", eventType.Name);
                cmd.Parameters.AddWithValue("cutoff", (DateTimeOffset.UtcNow - eventType.Retention!.Value).ToString("O"));
                cmd.Parameters.AddWithValue("batch", batchSize);

                var batch = await cmd.ExecuteNonQueryAsync(ct);
                deleted += batch;
                if (batch > 0)
                {
                    onBatchCommitted?.Invoke(batch);
                }

                if (batch < batchSize)
                {
                    break;
                }
            }
        }

        return deleted;
    }
}
