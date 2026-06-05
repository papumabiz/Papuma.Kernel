// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Provides extension methods for setting scope context variables on a PostgreSQL connection.
/// </summary>
public static class ScopeConnectionExtensions
{
    /// <summary>
    /// Sets <c>app.current_scope</c> and <c>app.current_tenant</c> as <c>SET LOCAL</c>
    /// session variables on the connection. <c>SET LOCAL</c> scopes the values to the
    /// current transaction; callers must ensure a transaction is active.
    /// </summary>
    /// <param name="connection">The open PostgreSQL connection.</param>
    /// <param name="scope">The scope context to apply.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task SetScopeAsync(
        this NpgsqlConnection connection,
        ScopeContext scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scope);

        await using (var scopeCmd = connection.CreateCommand())
        {
            scopeCmd.CommandText = "SET LOCAL app.current_scope = @scope";
            scopeCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            await scopeCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var tenantCmd = connection.CreateCommand())
        {
            tenantCmd.CommandText = "SET LOCAL app.current_tenant = @tenantId";
            tenantCmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
            await tenantCmd.ExecuteNonQueryAsync(ct);
        }
    }
}
