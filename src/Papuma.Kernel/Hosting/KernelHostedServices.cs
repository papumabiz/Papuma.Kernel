// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Applies the kernel schema (including declared key indexes), then the application's
/// <see cref="ISchemaContributor"/>s, on startup when
/// <see cref="PapumaKernelOptions.EnsureSchema"/> is enabled — before the feed workers,
/// which are registered after this service. With a hash key configured it also verifies the
/// key against the database (ADR-025), whether or not the schema is ensured here.
/// </summary>
internal sealed class KernelSchemaInitializer(
    NpgsqlDataSource dataSource,
    KernelModel model,
    PapumaKernelOptions options,
    IEnumerable<ISchemaContributor> contributors,
    IHashKeyProvider? hashKeys = null,
    ILogger<KernelSchemaInitializer>? logger = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Fail closed before any work: a Hash policy without a key must stop the host (ADR-025).
        model.EnsureHashKeysConfigured(hashKeys);

        if (options.EnsureSchema)
        {
            await SchemaManager.EnsureSchemaAsync(dataSource, model, logger, cancellationToken);
        }

        if (hashKeys is not null)
        {
            // Also when the schema is applied by a separate deployment step: a wrong key must stop
            // an instance before it writes a single hash (ADR-025).
            await SchemaManager.EnsureHashKeyAsync(dataSource, hashKeys, cancellationToken);
        }

        if (!options.EnsureSchema)
        {
            return;
        }

        foreach (var contributor in contributors)
        {
            await contributor.EnsureSchemaAsync(dataSource, cancellationToken);
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
        FeedLoop.RunHostedAsync("Change feed", processor.HasHandlers, processor.RunAsync, stoppingToken);
}

/// <summary>
/// Hosts the event log engine (ADR-013) as a background worker.
/// </summary>
internal sealed class EventFeedHostedService(EventFeedProcessor processor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        FeedLoop.RunHostedAsync("Event feed", processor.HasHandlers, processor.RunAsync, stoppingToken);
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
                var deleted = await EventRetention.PurgeExpiredAsync(
                    dataSource, model, EventRetention.DefaultBatchSize, onBatchCommitted: null, stoppingToken, _logger);
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
