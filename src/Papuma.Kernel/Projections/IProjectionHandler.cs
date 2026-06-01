// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Handles change feed records and projects them into a read model within the same transaction.
/// </summary>
public interface IProjectionHandler
{
    /// <summary>
    /// Gets the unique projection name used for checkpoints and failure tracking.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the event types consumed by this projection.
    /// </summary>
    IReadOnlyCollection<string> EventTypes { get; }

    /// <summary>
    /// Applies the supplied change record within the provided transaction.
    /// </summary>
    /// <param name="record">The change record to handle.</param>
    /// <param name="connection">The database connection used by the worker.</param>
    /// <param name="transaction">The transaction that scopes handler writes and checkpoint updates.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleAsync(
        ChangeRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default);
}