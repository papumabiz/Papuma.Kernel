// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Configures retention cleanup behavior for redacted records in the unified event feed.
/// </summary>
public sealed class RetentionWorkerOptions
{
    /// <summary>
    /// Gets or sets the idle wait time between cleanup cycles.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the retention window. Redacted rows older than now minus this window are eligible for deletion.
    /// </summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Gets or sets the maximum number of rows deleted per cycle.
    /// </summary>
    public int BatchSize { get; set; } = 1000;
}
