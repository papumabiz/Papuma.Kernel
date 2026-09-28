// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Processing;

/// <summary>
/// A point-in-time lag snapshot for one change handler.
/// </summary>
/// <param name="HandlerName">The handler name.</param>
/// <param name="Checkpoint">The highest <c>seq</c> delivered to the handler so far.</param>
/// <param name="LatestSeq">The highest <c>seq</c> in the feed.</param>
/// <param name="Lag">The number of committed changes the handler has not received yet.</param>
public sealed record ChangeFeedLagSnapshot(
    string HandlerName,
    long Checkpoint,
    long LatestSeq,
    long Lag)
{
    /// <summary>
    /// Gets the projection version the handler declares (<c>IProjection</c>, ADR-024), or
    /// <c>null</c> for an effect handler.
    /// </summary>
    public int? ProjectionVersion { get; init; }

    /// <summary>
    /// Gets whether this processor pauses the projection: its stored version is higher
    /// than the declared one — a newer deploy has rebuilt it, and this instance runs older
    /// code (ADR-024).
    /// </summary>
    public bool Paused { get; init; }
}
