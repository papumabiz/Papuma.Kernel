// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Projections;

/// <summary>
/// Allows a projection to reset its read model before a replay starts.
/// </summary>
public interface IReplayableProjection
{
    /// <summary>
    /// Prepares the projection for a full replay.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    Task PrepareReplayAsync(CancellationToken ct = default);
}