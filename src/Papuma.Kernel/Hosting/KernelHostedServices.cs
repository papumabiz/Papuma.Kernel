// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Applies the kernel schema (including declared key indexes) on startup when
/// <see cref="PapumaKernelOptions.EnsureSchema"/> is enabled.
/// </summary>
internal sealed class KernelSchemaInitializer(
    NpgsqlDataSource dataSource,
    KernelModel model,
    PapumaKernelOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.EnsureSchema)
        {
            await SchemaManager.EnsureSchemaAsync(dataSource, model, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Hosts the change feed engine (ADR-009/010) as a background worker.
/// </summary>
internal sealed class ChangeFeedHostedService(ChangeFeedProcessor processor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        processor.RunAsync(stoppingToken);
}

/// <summary>
/// Hosts the event log engine (ADR-013) as a background worker.
/// </summary>
internal sealed class EventFeedHostedService(EventFeedProcessor processor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        processor.RunAsync(stoppingToken);
}

/// <summary>
/// Purges expired events on a fixed interval — only active when at least one event
/// type declares a retention (ADR-013).
/// </summary>
internal sealed class EventRetentionHostedService(
    NpgsqlDataSource dataSource,
    KernelModel model,
    PapumaKernelOptions options,
    ILogger<EventRetentionHostedService>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<EventRetentionHostedService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!model.EventTypes.Any(e => e.Retention is not null))
        {
            return; // retention is opt-in per event type
        }

        using var timer = new PeriodicTimer(options.RetentionInterval);
        while (await TickAsync(timer, stoppingToken))
        {
            try
            {
                var deleted = await EventRetention.PurgeExpiredAsync(dataSource, model, stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation("Event retention purged {Deleted} expired events.", deleted);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event retention cycle failed.");
            }
        }
    }

    private static async Task<bool> TickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
