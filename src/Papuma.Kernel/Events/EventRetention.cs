// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Events;

/// <summary>
/// Purges events past their type-specific retention (ADR-013): the event log is a
/// fact store, not a version store — typ-spezifische Löschung ist legitim. Retention
/// is opt-in per event type (<c>Event&lt;T&gt;(e =&gt; e.Retention(...))</c>); types
/// without retention are never touched.
/// </summary>
/// <remarks>
/// Retention periods should be much longer than consumer lag — purged events are gone
/// for late consumers and rebuilds alike. Run from a periodic job (phase 9 hosting).
/// </remarks>
public static class EventRetention
{
    /// <summary>
    /// Deletes all events of retention-configured types that are older than their
    /// retention period. Returns the number of deleted events.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="model">The kernel model carrying the retention configuration.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<int> PurgeExpiredAsync(
        NpgsqlDataSource dataSource,
        KernelModel model,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(model);

        var deleted = 0;
        foreach (var eventType in model.EventTypes.Where(e => e.Retention is not null))
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM papuma.event
                WHERE event_type = @type
                  AND occurred_at < now() - @retention
                """;
            cmd.Parameters.AddWithValue("type", eventType.Name);
            cmd.Parameters.AddWithValue("retention", eventType.Retention!.Value);

            deleted += await cmd.ExecuteNonQueryAsync(ct);
        }

        return deleted;
    }
}
