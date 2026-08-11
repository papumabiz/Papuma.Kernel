// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Bootstrap for the SQLite-backed Papuma kernel — counterpart of <c>AddPapumaKernel</c>,
/// mirroring its registration shape for a consumer switching between the two feels
/// identical: same options shape, same ordered-hosted-service startup, same
/// <c>.AddChangeHandler&lt;T&gt;()</c>/<c>.AddEventHandler&lt;T&gt;()</c> chaining.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite kernel: <see cref="SqliteDocumentStore"/>,
    /// <see cref="KernelModel"/>, the feed processors, the in-process wakeup notifier, and
    /// hosted services for schema setup, feed processing and event retention. Register
    /// handlers via the returned builder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The kernel configuration.</param>
    public static PapumaKernelLocalBuilder AddPapumaKernelLocal(
        this IServiceCollection services,
        Action<PapumaKernelLocalOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PapumaKernelLocalOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);

        services.AddSingleton(_ =>
        {
            var builder = new KernelModelBuilder();
            options.ModelConfiguration!(builder);
            return builder.Build();
        });

        services.AddSingleton<SqliteChangeNotifier>();

        services.AddSingleton(sp => new SqliteDocumentStore(
            options.ResolveConnectionString(),
            sp.GetRequiredService<KernelModel>(),
            sp.GetRequiredService<SqliteChangeNotifier>().TrySignal));

        services.AddSingleton(sp => new SqliteChangeFeedProcessor(
            options.ResolveConnectionString(),
            sp.GetServices<IChangeHandler>(),
            sp.GetRequiredService<SqliteChangeNotifier>(),
            options.Processing,
            sp.GetService<ILogger<SqliteChangeFeedProcessor>>()));

        services.AddSingleton(sp => new SqliteEventFeedProcessor(
            options.ResolveConnectionString(),
            sp.GetServices<IEventHandler>(),
            sp.GetRequiredService<SqliteChangeNotifier>(),
            options.Processing,
            sp.GetService<ILogger<SqliteEventFeedProcessor>>()));

        // Order matters: the schema initializer's StartAsync completes before the
        // background workers start (sequential IHostedService startup).
        services.AddHostedService<SqliteKernelSchemaInitializer>();
        services.AddHostedService<SqliteChangeFeedHostedService>();
        services.AddHostedService<SqliteEventFeedHostedService>();
        services.AddHostedService<SqliteEventRetentionHostedService>();

        return new PapumaKernelLocalBuilder(services);
    }
}
