// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Bootstrap for the Papuma kernel (architecture §3): data source, metamodel,
/// schema management, store, and hosted feed workers.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the kernel: <see cref="DocumentStore"/>, <see cref="KernelModel"/>,
    /// the feed processors, and hosted services for schema setup, feed processing and
    /// event retention. Register handlers via the returned builder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The kernel configuration.</param>
    public static PapumaKernelBuilder AddPapumaKernel(
        this IServiceCollection services,
        Action<PapumaKernelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PapumaKernelOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);

        if (options.DataSource is not null)
        {
            // Externally owned instance — the container must not dispose it.
            services.AddSingleton(options.DataSource);
        }
        else
        {
            // Kernel-owned — created and disposed by the container.
            services.AddSingleton(_ => NpgsqlDataSource.Create(options.ConnectionString!));
        }

        services.AddSingleton(_ =>
        {
            var builder = new KernelModelBuilder();
            options.ModelConfiguration!(builder);
            return builder.Build();
        });

        services.AddSingleton(sp => new DocumentStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<KernelModel>(),
            sp.GetService<ILogger<DocumentStore>>()));

        services.AddSingleton(sp => new ChangeFeedProcessor(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetServices<IChangeHandler>(),
            options.Processing,
            sp.GetService<ILogger<ChangeFeedProcessor>>()));

        services.AddSingleton(sp => new EventFeedProcessor(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetServices<IEventHandler>(),
            options.Processing,
            sp.GetService<ILogger<EventFeedProcessor>>()));

        // Order matters: the schema initializer's StartAsync completes before the
        // background workers start (sequential IHostedService startup).
        services.AddHostedService<KernelSchemaInitializer>();
        if (options.RunFeedWorkers)
        {
            services.AddHostedService<ChangeFeedHostedService>();
            services.AddHostedService<EventFeedHostedService>();
        }

        services.AddHostedService<EventRetentionHostedService>();

        return new PapumaKernelBuilder(services);
    }
}
