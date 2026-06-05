// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Configures retention cleanup behavior for redacted records.
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
    /// Gets or sets the maximum number of rows deleted per table and cycle.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets a value indicating whether redacted rows in change_feed are eligible for cleanup.
    /// </summary>
    public bool DeleteFromChangeFeed { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether redacted rows in business_event_log are eligible for cleanup.
    /// Defaults to false because business_event_log can be used as long-term audit/event history.
    /// </summary>
    public bool DeleteFromBusinessEventLog { get; set; } = false;
}