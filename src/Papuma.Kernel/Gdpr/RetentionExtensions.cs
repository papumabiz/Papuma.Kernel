// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Registers retention services with dependency injection.
/// </summary>
public static class RetentionExtensions
{
    /// <summary>
    /// Registers a retention worker that removes old redacted rows.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional retention worker configuration callback.</param>
    /// <param name="scopeFilter">Optional scope filter for this worker.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddRetentionWorker(
        this IServiceCollection services,
        Action<RetentionWorkerOptions>? configure = null,
        ScopeFilter? scopeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostedService>(sp =>
        {
            var options = new RetentionWorkerOptions();
            configure?.Invoke(options);

            return new RetentionWorker(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<RetentionWorker>>(),
                options,
                scopeFilter);
        });

        return services;
    }
}