// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Events;
using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Transactions;

namespace Papuma.Kernel;

/// <summary>
/// Convenience extensions for registering all Papuma.Kernel services at once.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core Papuma.Kernel services: <see cref="ChangeWriter"/>,
    /// <see cref="ChangeFeedReader"/>,
    /// <see cref="BusinessEventWriter"/>, <see cref="OutboxWriter"/>,
    /// <see cref="GdprProcessor"/> and <see cref="IUnitOfWork"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure kernel options.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddPapumaKernel(
        this IServiceCollection services,
        Action<PapumaKernelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new PapumaKernelOptions();
        configure?.Invoke(options);

        services.AddSingleton(new ChangeWriterOptions
        {
            MaxPayloadSizeBytes = options.MaxPayloadSizeBytes,
        });

        services.AddSingleton(new BusinessEventWriterOptions
        {
            MaxPayloadSizeBytes = options.MaxPayloadSizeBytes,
        });

        services.AddSingleton(new OutboxWriterOptions
        {
            MaxPayloadSizeBytes = options.MaxPayloadSizeBytes,
        });

        services.AddSingleton<ChangeWriter>(sp =>
            new ChangeWriter(sp.GetRequiredService<ChangeWriterOptions>()));

        services.AddSingleton<ChangeFeedReader>();

        services.AddSingleton<BusinessEventWriter>(sp =>
            new BusinessEventWriter(sp.GetRequiredService<BusinessEventWriterOptions>()));

        services.AddSingleton<OutboxWriter>(sp =>
            new OutboxWriter(sp.GetRequiredService<OutboxWriterOptions>()));

        services.AddSingleton<GdprProcessor>(sp =>
            new GdprProcessor(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<BusinessEventWriter>(),
                sp.GetRequiredService<ILogger<GdprProcessor>>()));

        services.AddSingleton<IUnitOfWork>(sp =>
            new NpgsqlUnitOfWork(
                sp.GetRequiredService<NpgsqlDataSource>(),
                options.UnitOfWork,
                sp.GetService<ILogger<NpgsqlUnitOfWork>>()));

        return services;
    }
}
