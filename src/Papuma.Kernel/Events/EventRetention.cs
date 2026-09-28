// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
/// The purge runs inside row-level security: it finds the affected tenants under the
/// <c>All</c> scope (which reads, never writes) and deletes under each tenant's scope.
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
            void Bind(NpgsqlParameterCollection p)
            {
                p.AddWithValue("type", eventType.Name);
                p.AddWithValue("retention", eventType.Retention!.Value);
            }

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var scopes = await ScopedMaintenance.ReadScopesAsync(conn, tx, """
                SELECT DISTINCT scope, tenant_id FROM papuma.event
                WHERE event_type = @type AND occurred_at < now() - @retention
                """, Bind, ct);
            deleted += await ScopedMaintenance.ExecutePerScopeAsync(conn, tx, scopes, """
                DELETE FROM papuma.event
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND event_type = @type AND occurred_at < now() - @retention
                """, Bind, ct);
            await tx.CommitAsync(ct);
        }

        return deleted;
    }
}
