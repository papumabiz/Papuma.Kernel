// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Processing;

/// <summary>
/// A point-in-time lag snapshot for one change handler.
/// </summary>
/// <param name="HandlerName">The handler name.</param>
/// <param name="Checkpoint">The persisted checkpoint sequence.</param>
/// <param name="LatestSeq">The latest stable-visible feed sequence.</param>
/// <param name="Lag">The non-negative lag (<c>LatestSeq - Checkpoint</c>).</param>
public sealed record ChangeFeedLagSnapshot(
    string HandlerName,
    long Checkpoint,
    long LatestSeq,
    long Lag);
