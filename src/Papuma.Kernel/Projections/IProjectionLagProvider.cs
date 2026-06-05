// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Projections;

/// <summary>
/// Provides lag snapshots for a projection worker.
/// </summary>
public interface IProjectionLagProvider
{
    /// <summary>
    /// Gets the unique projection name.
    /// </summary>
    string ProjectionName { get; }

    /// <summary>
    /// Gets a lag snapshot for the projection.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The current lag snapshot.</returns>
    Task<ProjectionLagSnapshot> GetLagSnapshotAsync(CancellationToken ct = default);
}