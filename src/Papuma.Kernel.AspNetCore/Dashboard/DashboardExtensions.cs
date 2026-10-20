// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    // Mount path: absolute, URL-safe segments only. Keeps the path usable inside a JS
    // string literal and a route template without further escaping.
    private static readonly Regex PathPattern = new(
        "^/[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
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
    /// — protect it like a health endpoint: <c>MapPapumaDashboard(...).RequireAuthorization(...)</c>,
    /// <c>.RequireHost(...)</c>, an authorization fallback policy, or an internal-only host.
    /// If none of these is present the kernel logs one error (category
    /// <c>Papuma.Kernel.Dashboard</c>) when the endpoint table is built — in practice on the
    /// first request, not when the host starts. The check is a nudge, not a guard: it
    /// does not block the endpoint and cannot see a reverse proxy or network rule.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">
    /// The base path of the dashboard: absolute, made of letters, digits, <c>_</c> and <c>-</c>
    /// segments (for example <c>/papuma</c>). <c>/</c> is rejected so the dashboard never
    /// claims the application root.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a valid mount path.</exception>
    public static IEndpointConventionBuilder MapPapumaDashboard(
        this IEndpointRouteBuilder endpoints,
        string path = "/papuma")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.TrimEnd('/');
        if (!PathPattern.IsMatch(path))
        {
            throw new ArgumentException(
                "The dashboard path must be absolute and consist of letters, digits, '_' and '-' segments (e.g. '/papuma'); '/' is not allowed.",
                nameof(path));
        }

        // Resolving the collector here (not lazily per request) starts the meter
        // listener with the app, so totals cover the full process lifetime.
        var collector = endpoints.ServiceProvider.GetRequiredService<DashboardMetricsCollector>();

        var group = endpoints.MapGroup(path);

        group.MapGet("/", (HttpContext http) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            ApplySecurityHeaders(http.Response, nonce);
            return Results.Content(DashboardPage.Html($"{path}/data", nonce), "text/html");
        });

        group.MapGet("/data", async (
            HttpContext http,
            ChangeFeedProcessor changeProcessor,
            EventFeedProcessor eventProcessor,
            CancellationToken ct) =>
        {
            ApplySecurityHeaders(http.Response, nonce: null);
            var json = await DashboardDataBuilder.BuildAsync(changeProcessor, eventProcessor, collector, ct);
            return Results.Content(json.ToJsonString(), "application/json");
        });

        // Pit-of-success nudge (security review M2, A-3): the dashboard exposes operational
        // metadata. A Finally convention runs after any chained .RequireAuthorization() or
        // .RequireHost(), so we see the final endpoint metadata. It runs when the endpoint
        // table is built — in practice on the first request, NOT at host start — and once per
        // endpoint, hence the flag: one log entry per MapPapumaDashboard call.
        var reported = 0;
        ((IEndpointConventionBuilder)group).Finally(builder =>
        {
            if (IsProtected(builder.Metadata, endpoints.ServiceProvider)
                || Interlocked.Exchange(ref reported, 1) == 1)
            {
                return;
            }

            endpoints.ServiceProvider.GetService<ILoggerFactory>()?
                .CreateLogger("Papuma.Kernel.Dashboard")
                .LogError(
                    "Papuma dashboard mapped at '{Path}' without protection. It exposes handler " +
                    "names, lag, failures and error messages. Chain .RequireAuthorization() or " +
                    ".RequireHost(...) on MapPapumaDashboard(...), configure an authorization " +
                    "FallbackPolicy, or bind it to an internal-only endpoint.", path);
        });

        return group;
    }

    /// <summary>
    /// Whether the endpoint carries a guard the host put in front of it: authorization
    /// metadata (an explicit <c>AllowAnonymous</c> wins over it, as in the authorization
    /// middleware), a host restriction, or an authorization fallback policy that applies
    /// to endpoints without authorization metadata.
    /// </summary>
    private static bool IsProtected(IList<object> metadata, IServiceProvider services)
    {
        // RequireHost("*") restricts nothing.
        if (metadata.OfType<IHostMetadata>().Any(h => h.Hosts.Any(host => host != "*")))
        {
            return true;
        }

        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return false;
        }

        if (metadata.OfType<IAuthorizeData>().Any()
            || metadata.OfType<IAuthorizationRequirementData>().Any()
            || metadata.OfType<AuthorizationPolicy>().Any())
        {
            return true;
        }

        // Endpoint conventions are synchronous; the default provider completes synchronously.
        var provider = services.GetService<IAuthorizationPolicyProvider>();
        return provider is not null && provider.GetFallbackPolicyAsync().GetAwaiter().GetResult() is not null;
    }

    /// <summary>
    /// Hardens the page and data responses: the page may only run its own nonce-tagged
    /// script and style, can only talk to its own origin and cannot be framed; nothing
    /// is cached because the content is operational metadata.
    /// </summary>
    private static void ApplySecurityHeaders(HttpResponse response, string? nonce)
    {
        // style-src-attr: the page sets a few inline style attributes (bar widths); a
        // nonce cannot cover attributes. Scripts stay nonce-only.
        var csp = nonce is null
            ? "default-src 'none'; frame-ancestors 'none'; base-uri 'none'"
            : $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; " +
              "style-src-attr 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; " +
              "base-uri 'none'; form-action 'none'";

        var headers = response.Headers;
        headers["Content-Security-Policy"] = csp;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
    }
}
