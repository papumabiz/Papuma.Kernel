// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Events;

/// <summary>
/// Purges events past their type-specific retention — SQLite counterpart of
/// <c>EventRetention</c>. Retention is opt-in per event type
/// (<c>Event&lt;T&gt;(e =&gt; e.Retention(...))</c>); types without retention are never
/// touched.
/// </summary>
public static class SqliteEventRetention
{
    /// <summary>
    /// Deletes all events of retention-configured types that are older than their
    /// retention period. Returns the number of deleted events.
    /// </summary>
    public static async Task<int> PurgeExpiredAsync(
        string connectionString,
        KernelModel model,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(model);

        var deleted = 0;
        foreach (var eventType in model.EventTypes.Where(e => e.Retention is not null))
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM event WHERE event_type = @type AND occurred_at < @cutoff";
            cmd.Parameters.AddWithValue("type", eventType.Name);
            cmd.Parameters.AddWithValue("cutoff", (DateTimeOffset.UtcNow - eventType.Retention!.Value).ToString("O"));

            deleted += await cmd.ExecuteNonQueryAsync(ct);
        }

        return deleted;
    }
}
