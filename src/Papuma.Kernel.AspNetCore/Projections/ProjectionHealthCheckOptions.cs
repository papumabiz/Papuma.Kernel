// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.AspNetCore.Projections;

/// <summary>
/// Configures projection lag health check behavior.
/// </summary>
public sealed class ProjectionHealthCheckOptions
{
    /// <summary>
    /// Gets or sets the maximum tolerated lag before the health check becomes unhealthy.
    /// </summary>
    public long MaxAllowedLag { get; set; } = 1000;
}