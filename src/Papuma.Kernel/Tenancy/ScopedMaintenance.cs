// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
    /// <c>scope, tenant_id</c>. Sets the <c>All</c> scope on the transaction first.
    /// </summary>
    public static async Task<IReadOnlyList<ScopeContext>> ReadScopesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql,
        Action<NpgsqlParameterCollection>? bind, CancellationToken ct)
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
            scopes.Add(FromRow(reader.GetString(0), reader.GetString(1)));
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

    /// <summary>The scope of a stored row (<c>scope</c>, <c>tenant_id</c> columns).</summary>
    public static ScopeContext FromRow(string scope, string tenantId) =>
        scope == nameof(ScopeType.Tenant) ? ScopeContext.Tenant(tenantId) : ScopeContext.Platform();
}
