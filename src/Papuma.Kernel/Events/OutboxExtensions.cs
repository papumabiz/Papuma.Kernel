// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// Registers outbox handlers and their worker with dependency injection.
/// </summary>
public static class OutboxExtensions
{
    /// <summary>
    /// Registers an outbox publisher and its hosted worker.
    /// </summary>
    /// <typeparam name="TPublisher">The outbox publisher type.</typeparam>
    /// <param name="services">The service collection to add the outbox worker to.</param>
    /// <param name="configure">Optional worker configuration callback.</param>
    /// <param name="scope">Optional scope filter for the worker.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddOutboxWorker<TPublisher>(
        this IServiceCollection services,
        Action<OutboxWorkerOptions>? configure = null,
        ScopeContext? scope = null)
        where TPublisher : class, IOutboxPublisher
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TPublisher>();
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = new OutboxWorkerOptions();
            configure?.Invoke(options);

            return new OutboxWorker(
                sp.GetRequiredService<TPublisher>(),
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ILogger<OutboxWorker>>(),
                options,
                scope);
        });

        return services;
    }
}