// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Transactions;

using Npgsql;

/// <summary>
/// Defines a transactional execution boundary for database operations.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Executes the specified action in a single database transaction.
    /// </summary>
    /// <param name="action">Action to execute with connection and transaction context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the action is committed or fails when execution aborts.</returns>
    Task ExecuteAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action,
        CancellationToken ct = default);
}