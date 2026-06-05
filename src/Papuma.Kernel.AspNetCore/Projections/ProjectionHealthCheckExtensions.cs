// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Papuma.Kernel.AspNetCore.Projections;

/// <summary>
/// Provides projection lag health check registration extensions.
/// </summary>
public static class ProjectionHealthCheckExtensions
{
    /// <summary>
    /// Registers projection lag health checks.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional projection health check configuration callback.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddPapumaProjectionHealthChecks(
        this IServiceCollection services,
        Action<ProjectionHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ProjectionHealthCheckOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<Microsoft.Extensions.Options.IOptions<ProjectionHealthCheckOptions>>(
            _ => Microsoft.Extensions.Options.Options.Create(options));

        services
            .AddHealthChecks()
            .AddCheck<ProjectionLagHealthCheck>("papuma.projections", HealthStatus.Unhealthy);

        return services;
    }
}