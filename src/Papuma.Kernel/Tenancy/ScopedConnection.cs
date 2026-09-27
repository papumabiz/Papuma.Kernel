// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// A connection with an open transaction whose scope is already set — the safe way to
/// read or write application tables guarded by <c>papuma.scope_visible</c> /
/// <c>papuma.scope_writable</c> (ADR-019) outside a document session. Open it with
/// <see cref="ScopedConnectionExtensions.OpenScopedAsync"/>.
/// </summary>
/// <remarks>
/// Commands from <see cref="CreateCommand"/> already carry the transaction, so none can
/// run outside the scope by accident. Disposing without <see cref="CommitAsync"/> rolls
/// back — the right ending for reads; call <see cref="CommitAsync"/> after writes.
/// </remarks>
public sealed class ScopedConnection : IAsyncDisposable
{
    private bool _completed;

    private ScopedConnection(NpgsqlConnection connection, NpgsqlTransaction transaction, ScopeContext scope)
    {
        Connection = connection;
        Transaction = transaction;
        Scope = scope;
    }

    /// <summary>Gets the open connection.</summary>
    public NpgsqlConnection Connection { get; }

    /// <summary>Gets the transaction the scope is confined to.</summary>
    public NpgsqlTransaction Transaction { get; }

    /// <summary>Gets the scope set on <see cref="Transaction"/>.</summary>
    public ScopeContext Scope { get; }

    /// <summary>
    /// Creates a command bound to <see cref="Transaction"/>, so it runs under the scope.
    /// </summary>
    /// <param name="commandText">The SQL, or <c>null</c> to set it later.</param>
    /// <exception cref="InvalidOperationException">The transaction was already committed.</exception>
    public NpgsqlCommand CreateCommand(string? commandText = null)
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The scoped transaction was committed; open a new scoped connection for further commands.");
        }

        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        if (commandText is not null)
        {
            command.CommandText = commandText;
        }

        return command;
    }

    /// <summary>Commits the transaction. Further commands need a new scoped connection.</summary>
    /// <param name="ct">A cancellation token.</param>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        await Transaction.CommitAsync(ct);
        _completed = true;
    }

    /// <summary>Rolls back unless committed, and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync(); // rolls back an uncommitted transaction
        await Connection.DisposeAsync();
    }

    internal static async Task<ScopedConnection> OpenAsync(
        NpgsqlDataSource dataSource, ScopeContext scope, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            await connection.SetScopeAsync(transaction, scope, ct);
            return new ScopedConnection(connection, transaction, scope);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>Opens <see cref="ScopedConnection"/>s.</summary>
public static class ScopedConnectionExtensions
{
    /// <summary>
    /// Opens a connection, begins a transaction and sets <paramref name="scope"/> on it
    /// (<see cref="ScopeConnectionExtensions.SetScopeAsync"/>) — in one call, for reading
    /// and writing application tables under RLS (ADR-019, recipe
    /// <c>same-database-read-models.md</c>).
    /// </summary>
    /// <param name="dataSource">The data source of the application role.</param>
    /// <param name="scope">The scope to confine the transaction to.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The scoped connection; dispose it (rolls back unless committed).</returns>
    public static Task<ScopedConnection> OpenScopedAsync(
        this NpgsqlDataSource dataSource, ScopeContext scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(scope);
        return ScopedConnection.OpenAsync(dataSource, scope, ct);
    }
}
