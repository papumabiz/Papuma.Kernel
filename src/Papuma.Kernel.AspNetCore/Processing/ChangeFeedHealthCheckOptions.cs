// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.AspNetCore.Processing;

/// <summary>
/// Configures the change feed lag health check.
/// </summary>
public sealed class ChangeFeedHealthCheckOptions
{
    /// <summary>
    /// Gets or sets the maximum tolerated lag (in feed sequence numbers) before the
    /// health check reports unhealthy.
    /// </summary>
    public long MaxAllowedLag { get; set; } = 1000;
}
