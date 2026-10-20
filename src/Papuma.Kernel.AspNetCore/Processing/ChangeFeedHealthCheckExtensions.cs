// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
    /// <remarks>
    /// <see cref="ChangeFeedHealthCheckOptions"/> are validated when the host starts: a
    /// <c>MaxAllowedLag</c> of zero or less set through <c>Configure</c> or configuration binding
    /// fails the start with an <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="maxAllowedLag">The maximum tolerated lag: committed changes a handler has not received yet.</param>
    /// <param name="name">The health check name.</param>
    public static IHealthChecksBuilder AddPapumaChangeFeedLag(
        this IHealthChecksBuilder builder,
        long maxAllowedLag = 1000,
        string name = "papuma_change_feed")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAllowedLag);

        // Validated on start (and on first use) so a later Configure(...) or a bound
        // configuration section cannot slip an invalid threshold past the argument guard above.
        builder.Services.AddOptions<ChangeFeedHealthCheckOptions>()
            .Configure(o => o.MaxAllowedLag = maxAllowedLag)
            .Validate(o => o.MaxAllowedLag > 0, "MaxAllowedLag must be greater than zero.")
            .ValidateOnStart();
        return builder.AddCheck<ChangeFeedLagHealthCheck>(name);
    }
}
