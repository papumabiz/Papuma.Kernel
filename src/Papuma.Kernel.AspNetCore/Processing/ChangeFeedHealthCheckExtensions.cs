// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;

namespace Papuma.Kernel.AspNetCore.Processing;

/// <summary>
/// Health check registration for the change feed lag metric.
/// </summary>
public static class ChangeFeedHealthCheckExtensions
{
    /// <summary>
    /// Adds a health check that reports unhealthy when any change handler exceeds the
    /// lag threshold. Requires <c>AddPapumaKernel(...)</c>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="maxAllowedLag">The maximum tolerated lag in feed sequence numbers.</param>
    /// <param name="name">The health check name.</param>
    public static IHealthChecksBuilder AddPapumaChangeFeedLag(
        this IHealthChecksBuilder builder,
        long maxAllowedLag = 1000,
        string name = "papuma_change_feed")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAllowedLag);

        builder.Services.Configure<ChangeFeedHealthCheckOptions>(o => o.MaxAllowedLag = maxAllowedLag);
        return builder.AddCheck<ChangeFeedLagHealthCheck>(name);
    }
}
