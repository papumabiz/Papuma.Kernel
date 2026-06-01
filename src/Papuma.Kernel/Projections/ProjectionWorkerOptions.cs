// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Projections;

/// <summary>
/// Configures polling and retry behavior for a projection worker.
/// </summary>
public sealed class ProjectionWorkerOptions
{
    /// <summary>
    /// Gets or sets the idle wait time between polling cycles.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the maximum number of change records loaded per batch.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of failed attempts before an event is dead-lettered.
    /// </summary>
    public int MaxAttemptsPerEvent { get; set; } = 10;

    /// <summary>
    /// Gets or sets the base delay for exponential retry backoff.
    /// </summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum delay cap for exponential retry backoff.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}