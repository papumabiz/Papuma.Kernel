// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Projections;

/// <summary>
/// Coordinates replay requests for registered projection workers.
/// </summary>
public sealed class ReplayService
{
    private readonly IReadOnlyDictionary<string, ProjectionWorker> _workers;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplayService"/> class.
    /// </summary>
    /// <param name="workers">Projection workers indexed by projection name.</param>
    public ReplayService(IReadOnlyDictionary<string, ProjectionWorker> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        _workers = workers;
    }

    /// <summary>
    /// Requests a replay for the specified projection.
    /// </summary>
    /// <param name="projectionName">The unique projection name.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task RequestReplayAsync(string projectionName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectionName);

        if (!_workers.TryGetValue(projectionName, out var worker))
        {
            throw new InvalidOperationException($"Unknown projection: {projectionName}");
        }

        await worker.RequestReplayAsync(ct);
    }
}