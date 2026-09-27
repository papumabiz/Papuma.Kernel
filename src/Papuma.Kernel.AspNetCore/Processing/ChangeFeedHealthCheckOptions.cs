// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.AspNetCore.Processing;

/// <summary>
/// Configures the change feed lag health check.
/// </summary>
public sealed class ChangeFeedHealthCheckOptions
{
    /// <summary>
    /// Gets or sets the maximum tolerated lag — committed changes a handler has not
    /// received yet (ADR-022) — before the health check reports unhealthy.
    /// </summary>
    public long MaxAllowedLag { get; set; } = 1000;
}
