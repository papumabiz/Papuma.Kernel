// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Registers projection handlers and their workers with dependency injection.
/// </summary>
public static class ProjectionExtensions
{
    /// <summary>
    /// Registers a projection handler and its hosted worker.
    /// </summary>
    /// <typeparam name="THandler">The projection handler type.</typeparam>
    /// <param name="services">The service collection to add the projection to.</param>
    /// <param name="configure">Optional worker configuration callback.</param>
    /// <param name="scopeFilter">Optional scope filter for the worker.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddProjection<THandler>(
        this IServiceCollection services,
        Action<ProjectionWorkerOptions>? configure = null,
        ScopeFilter? scopeFilter = null)
        where THandler : class, IProjectionHandler
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<THandler>();
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = new ProjectionWorkerOptions();
            configure?.Invoke(options);

            return new ProjectionWorker(
                sp.GetRequiredService<THandler>(),
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<ProjectionWorker>>(),
                options,
                scopeFilter);
        });

        return services;
    }

    /// <summary>
    /// Registers an external projection handler and its hosted worker.
    /// </summary>
    /// <typeparam name="THandler">The external projection handler type.</typeparam>
    /// <param name="services">The service collection to add the projection to.</param>
    /// <param name="configure">Optional worker configuration callback.</param>
    /// <param name="scopeFilter">Optional scope filter for the worker.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddExternalProjection<THandler>(
        this IServiceCollection services,
        Action<ProjectionWorkerOptions>? configure = null,
        ScopeFilter? scopeFilter = null)
        where THandler : class, IExternalProjectionHandler
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<THandler>();
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = new ProjectionWorkerOptions();
            configure?.Invoke(options);

            return new ExternalProjectionWorker(
                sp.GetRequiredService<THandler>(),
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<ExternalProjectionWorker>>(),
                options,
                scopeFilter);
        });

        return services;
    }

    /// <summary>
    /// Registers a <see cref="ReplayService"/> that automatically discovers all
    /// <see cref="ProjectionWorker"/> instances registered as <see cref="IHostedService"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddReplayService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ReplayService>(sp =>
        {
            var workers = sp.GetServices<IHostedService>()
                .OfType<ProjectionWorker>()
                .ToDictionary(w => w.ProjectionName);

            return new ReplayService(workers);
        });

        return services;
    }
}