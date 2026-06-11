// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Processing;

/// <summary>
/// Configures the change feed processing engine (ADR-009/010).
/// </summary>
public sealed class ChangeFeedProcessorOptions
{
    /// <summary>Gets or sets the maximum number of changes loaded per handler per cycle.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum idle wait between poll cycles. NOTIFY wakes the
    /// processor earlier — polling stays the source of truth (ADR-010).
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the attempts before a failing change is skipped as poison.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Gets or sets the base delay of the exponential retry backoff.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the backoff ceiling.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "BatchSize must be >= 1.");
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be >= 1.");
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "PollInterval must be positive.");
        }

        if (BaseRetryDelay < TimeSpan.Zero || MaxRetryDelay < BaseRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseRetryDelay),
                "Retry delays must be non-negative and MaxRetryDelay >= BaseRetryDelay.");
        }
    }
}
