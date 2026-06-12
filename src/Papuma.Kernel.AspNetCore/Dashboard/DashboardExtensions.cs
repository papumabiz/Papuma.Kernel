// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.AspNetCore.Dashboard;

/// <summary>
/// The embedded first-look dashboard (Hangfire-style): one self-contained HTML page
/// over the phase-11 diagnostics APIs — lag per handler, failures, throughput rates.
/// A stopgap until real observability infrastructure (Prometheus/Grafana) exists,
/// and a quick glance even after.
/// </summary>
public static class DashboardExtensions
{
    /// <summary>
    /// Registers the in-process metrics collector. Call once at startup —
    /// the collector must listen from the beginning to see all measurements.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddPapumaDashboard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<DashboardMetricsCollector>();
        return services;
    }

    /// <summary>
    /// Maps the dashboard page at <paramref name="path"/> and its JSON data source at
    /// <c>{path}/data</c>. Requires <c>AddPapumaKernel(...)</c> and
    /// <c>AddPapumaDashboard()</c>.
    /// </summary>
    /// <remarks>
    /// The dashboard exposes operational metadata (handler names, lag, error messages)
    /// — protect it like a health endpoint: <c>MapPapumaDashboard(...).RequireAuthorization(...)</c>
    /// or bind it to an internal-only host.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The base path of the dashboard.</param>
    public static IEndpointConventionBuilder MapPapumaDashboard(
        this IEndpointRouteBuilder endpoints,
        string path = "/papuma")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.TrimEnd('/');

        // Resolving the collector here (not lazily per request) starts the meter
        // listener with the app, so totals cover the full process lifetime.
        var collector = endpoints.ServiceProvider.GetRequiredService<DashboardMetricsCollector>();

        var group = endpoints.MapGroup(path);

        group.MapGet("/", () => Results.Content(DashboardPage.Html($"{path}/data"), "text/html"));

        group.MapGet("/data", async (
            ChangeFeedProcessor changeProcessor,
            EventFeedProcessor eventProcessor,
            CancellationToken ct) =>
        {
            var json = await DashboardDataBuilder.BuildAsync(changeProcessor, eventProcessor, collector, ct);
            return Results.Content(json.ToJsonString(), "application/json");
        });

        return group;
    }
}
