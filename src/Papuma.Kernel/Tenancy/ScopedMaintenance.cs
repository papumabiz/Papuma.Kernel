// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Writes that span tenants — kernel maintenance such as event retention or the repair
/// after a logical restore — done inside the row-level security model (ADR-019): the
/// <c>All</c> scope reads every row and writes none, so the affected
/// <c>(scope, tenant_id)</c> pairs are read under it, and each pair is then written under
/// its own scope, in the same transaction.
/// </summary>
internal static class ScopedMaintenance
{
    /// <summary>
    /// Returns the distinct scopes named by <paramref name="sql"/>, which must select
    /// <c>scope, tenant_id</c>. Sets the <c>All</c> scope on the transaction first. A row whose
    /// tenant id is outside the whitelist (written past the kernel) cannot be written under any
    /// scope: it is skipped and logged, instead of failing the maintenance for every tenant.
    /// </summary>
    public static async Task<IReadOnlyList<ScopeContext>> ReadScopesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql,
        Action<NpgsqlParameterCollection>? bind, CancellationToken ct, ILogger? logger = null)
    {
        await conn.SetAllScopesAsync(tx, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        bind?.Invoke(cmd.Parameters);

        var scopes = new List<ScopeContext>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (scope, tenantId) = (reader.GetString(0), reader.GetString(1));
            if (TryFromRow(scope, tenantId, out var parsed))
            {
                scopes.Add(parsed);
            }
            else
            {
                LogSkippedScope(logger, scope, tenantId);
            }
        }

        return scopes;
    }

    /// <summary>
    /// Runs <paramref name="sql"/> once per scope, with that scope set on the transaction
    /// and bound as <c>@scope</c> / <c>@tenantId</c>; returns the affected rows in total.
    /// Leaves the last scope set.
    /// </summary>
    public static async Task<int> ExecutePerScopeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<ScopeContext> scopes, string sql,
        Action<NpgsqlParameterCollection>? bind, CancellationToken ct)
    {
        var affected = 0;
        foreach (var scope in scopes)
        {
            await conn.SetScopeAsync(tx, scope, ct);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
            bind?.Invoke(cmd.Parameters);
            affected += await cmd.ExecuteNonQueryAsync(ct);
        }

        return affected;
    }

    /// <summary>
    /// The scope of a stored row (<c>scope</c>, <c>tenant_id</c> columns); <c>false</c> for a tenant row
    /// whose tenant id is not a valid one.
    /// </summary>
    public static bool TryFromRow(string scope, string tenantId, [NotNullWhen(true)] out ScopeContext? result)
    {
        if (scope != nameof(ScopeType.Tenant))
        {
            result = ScopeContext.Platform();
            return true;
        }

        try
        {
            result = ScopeContext.Tenant(tenantId);
            return true;
        }
        catch (ArgumentException)
        {
            result = null;
            return false;
        }
    }

    /// <summary>Logs a row that was skipped for its invalid scope.</summary>
    public static void LogSkippedScope(ILogger? logger, string scope, string tenantId, string? what = null) =>
        logger?.LogWarning(
            "Skipping {What} stored under the invalid scope ({Scope}, tenant id {TenantId}): it cannot be written under " +
            "row-level security. It was written past the kernel; correct or remove it.",
            what ?? "rows", scope, Quote(tenantId));

    // Escapes control characters and bounds the length: the value comes from the database, not from the kernel.
    private static string Quote(string value) =>
        JsonSerializer.Serialize(value.Length > 100 ? value[..100] + "..." : value);
}
