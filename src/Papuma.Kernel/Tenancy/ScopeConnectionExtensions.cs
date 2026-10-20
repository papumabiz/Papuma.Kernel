// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Provides extension methods for setting scope context variables on a PostgreSQL connection.
/// </summary>
public static class ScopeConnectionExtensions
{
    /// <summary>
    /// Sets <c>app.current_scope</c> and <c>app.current_tenant</c> as PostgreSQL <c>SET LOCAL</c>
    /// session variables on the connection for the duration of the active transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Security model — two independent layers:</b>
    /// </para>
    /// <para>
    /// <b>Layer 1 (primary):</b> All SQL statements in this library carry explicit
    /// <c>WHERE scope = @scope AND tenant_id = @tenantId</c> predicates that are bound
    /// via parameterised queries. This layer is always active and does not depend on
    /// session state.
    /// </para>
    /// <para>
    /// <b>Layer 2 (defence-in-depth):</b> PostgreSQL Row Level Security (RLS) policies
    /// on all framework tables read <c>current_setting('app.current_scope')</c> and
    /// <c>current_setting('app.current_tenant')</c>. This method sets those variables
    /// using <c>SET LOCAL</c>, which confines them to the current transaction and
    /// automatically resets them on commit or rollback — preventing scope leaks across
    /// pooled connections.
    /// </para>
    /// <para>
    /// <b>Invariant:</b> This method must always be called <em>after</em>
    /// <c>BeginTransactionAsync</c> and <em>before</em> any data-access statement.
    /// Outside a transaction a transaction-local setting lasts only until the end of its
    /// statement (<c>SET LOCAL</c> merely warns) — it does not turn into a session-level one, so
    /// the scope is lost rather than leaked to the next caller of a pooled connection, and the
    /// RLS policies fail closed. The <c>transaction</c> parameter is required nevertheless: a
    /// scope that silently vanishes before the data-access statement is a bug, not a safe state.
    /// </para>
    /// <para>
    /// <b>Background workers that read across scopes</b> (<c>ScopeFilter.All()</c>) call
    /// <see cref="SetAllScopesAsync"/> instead — the kernel's feed processors do. No
    /// <c>BYPASSRLS</c> role is needed; the application role stays subject to RLS.
    /// </para>
    /// <para>
    /// <b>Application tables:</b> the same call scopes RLS policies on your own tables
    /// when they use <c>papuma.scope_visible</c> / <c>papuma.scope_writable</c>
    /// (ADR-019). Those functions are the contract — do not reference the setting names
    /// in your own policies.
    /// </para>
    /// </remarks>
    /// <param name="connection">The open PostgreSQL connection.</param>
    /// <param name="transaction">
    /// The active transaction the scope is confined to. Required — the scope GUCs are
    /// transaction-local (<c>set_config(..., true)</c>); without a transaction they would
    /// expire with the setting statement and the following statements would see no scope
    /// (fail closed, security review M4, S-16). Passing the transaction makes that impossible.
    /// </param>
    /// <param name="scope">The scope context to apply.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task SetScopeAsync(
        this NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ScopeContext scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(scope);

        // set_config(..., is_local: true) is the parameterizable equivalent of SET LOCAL;
        // plain SET LOCAL does not accept bind parameters in the extended protocol.
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT set_config('app.current_scope', @scope, true),
                   set_config('app.current_tenant', @tenantId, true)
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Sets <c>app.current_scope = 'All'</c> as a PostgreSQL <c>SET LOCAL</c> session variable
    /// on the connection for the duration of the active transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by background workers that process all scopes (i.e.
    /// <see cref="ScopeFilter.IsAll"/> is <c>true</c>). The <c>'All'</c> scope satisfies
    /// the RLS policies' read branch, which allows cross-scope reads without a
    /// <c>BYPASSRLS</c> database role.
    /// </para>
    /// <para>
    /// <b>Read-only by design:</b> the policies' <c>WITH CHECK</c> admits no write under
    /// <c>'All'</c>. A worker that writes scoped rows sets the row's own scope with
    /// <see cref="SetScopeAsync"/> for that write (ADR-019).
    /// </para>
    /// <para>
    /// <b>Invariant:</b> This method must always be called <em>after</em>
    /// <c>BeginTransactionAsync</c> and <em>before</em> any data-access statement.
    /// </para>
    /// </remarks>
    /// <param name="connection">The open PostgreSQL connection.</param>
    /// <param name="transaction">
    /// The active transaction the scope is confined to. Required for the same reason as
    /// <see cref="SetScopeAsync"/>: <c>SET LOCAL</c> outside a transaction is a no-op that
    /// leaves the following statements without a scope (fail closed, security review M4, S-16).
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task SetAllScopesAsync(
        this NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SET LOCAL app.current_scope = 'All'";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
