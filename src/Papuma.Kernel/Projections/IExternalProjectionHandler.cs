// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Handles change feed records for external systems where transactional coupling is not possible.
/// </summary>
public interface IExternalProjectionHandler
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
    /// Applies the supplied change record to an external target.
    /// </summary>
    /// <param name="record">The change record to handle.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleAsync(ChangeRecord record, CancellationToken ct = default);
}