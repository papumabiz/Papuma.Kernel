// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Papuma.Kernel.Projections;

namespace Papuma.Kernel.AspNetCore.Projections;

/// <summary>
/// Reports unhealthy status when one or more projection workers exceed the configured lag threshold.
/// </summary>
public sealed class ProjectionLagHealthCheck : IHealthCheck
{
    private readonly IEnumerable<IHostedService> _hostedServices;
    private readonly ProjectionHealthCheckOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectionLagHealthCheck"/> class.
    /// </summary>
    /// <param name="hostedServices">All hosted services from dependency injection.</param>
    /// <param name="options">The projection health check options.</param>
    public ProjectionLagHealthCheck(
        IEnumerable<IHostedService> hostedServices,
        IOptions<ProjectionHealthCheckOptions> options)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);
        ArgumentNullException.ThrowIfNull(options);

        _hostedServices = hostedServices;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var lagProviders = _hostedServices.OfType<IProjectionLagProvider>().ToArray();
        if (lagProviders.Length == 0)
        {
            return HealthCheckResult.Healthy("No projection workers are registered.");
        }

        var lagSnapshots = new List<ProjectionLagSnapshot>(lagProviders.Length);
        foreach (var provider in lagProviders)
        {
            lagSnapshots.Add(await provider.GetLagSnapshotAsync(cancellationToken));
        }

        var overloaded = lagSnapshots
            .Where(snapshot => snapshot.Lag > _options.MaxAllowedLag)
            .ToArray();

        if (overloaded.Length == 0)
        {
            return HealthCheckResult.Healthy($"All projection workers are below lag threshold ({_options.MaxAllowedLag}).");
        }

        var details = string.Join(", ",
            overloaded.Select(snapshot => $"{snapshot.ProjectionName}={snapshot.Lag}"));

        return HealthCheckResult.Unhealthy(
            $"Projection lag threshold exceeded ({_options.MaxAllowedLag}): {details}");
    }
}