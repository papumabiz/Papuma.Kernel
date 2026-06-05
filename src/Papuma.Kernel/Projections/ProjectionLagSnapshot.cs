// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Projections;

/// <summary>
/// Represents a point-in-time lag snapshot for a projection.
/// </summary>
/// <param name="ProjectionName">The unique projection name.</param>
/// <param name="Checkpoint">The current checkpoint sequence id.</param>
/// <param name="LatestSequenceId">The latest relevant change feed sequence id.</param>
/// <param name="Lag">The non-negative lag value (<c>LatestSequenceId - Checkpoint</c>).</param>
public sealed record ProjectionLagSnapshot(
    string ProjectionName,
    long Checkpoint,
    long LatestSequenceId,
    long Lag);