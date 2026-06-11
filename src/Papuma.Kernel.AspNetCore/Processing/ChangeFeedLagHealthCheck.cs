// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Papuma.Kernel.Processing;

namespace Papuma.Kernel.AspNetCore.Processing;

/// <summary>
/// Reports unhealthy status when one or more change handlers exceed the configured
/// lag threshold (architecture §8 / ADR-010 "feed lag" metric).
/// </summary>
public sealed class ChangeFeedLagHealthCheck : IHealthCheck
{
    private readonly ChangeFeedProcessor _processor;
    private readonly ChangeFeedHealthCheckOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeFeedLagHealthCheck"/> class.
    /// </summary>
    /// <param name="processor">The change feed processor.</param>
    /// <param name="options">The health check options.</param>
    public ChangeFeedLagHealthCheck(
        ChangeFeedProcessor processor,
        IOptions<ChangeFeedHealthCheckOptions> options)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(options);

        _processor = processor;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await _processor.GetLagAsync(cancellationToken);

        var overloaded = snapshots
            .Where(snapshot => snapshot.Lag > _options.MaxAllowedLag)
            .ToArray();

        if (overloaded.Length == 0)
        {
            return HealthCheckResult.Healthy(
                $"All change handlers are below the lag threshold ({_options.MaxAllowedLag}).");
        }

        var details = string.Join(", ", overloaded.Select(s => $"{s.HandlerName}={s.Lag}"));
        return HealthCheckResult.Unhealthy(
            $"Change feed lag threshold exceeded ({_options.MaxAllowedLag}): {details}");
    }
}
