// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Applies the SQLite kernel schema (including declared key indexes), then the
/// application's <see cref="ISqliteSchemaContributor"/>s, on startup when
/// <see cref="PapumaKernelLocalOptions.EnsureSchema"/> is enabled — before the feed
/// workers, which are registered after this service. Counterpart of
/// <c>KernelSchemaInitializer</c>.
/// </summary>
internal sealed class SqliteKernelSchemaInitializer(
    PapumaKernelLocalOptions options,
    KernelModel model,
    IEnumerable<ISqliteSchemaContributor> contributors) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.EnsureSchema)
        {
            return;
        }

        await using var connection = await SqliteConnectionFactory.OpenAsync(
            options.ResolveConnectionString(), cancellationToken);
        await SqliteSchemaManager.EnsureSchemaAsync(connection, model, cancellationToken);
        foreach (var contributor in contributors)
        {
            await contributor.EnsureSchemaAsync(connection, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Hosts the SQLite change feed engine as a background worker.
/// </summary>
internal sealed class SqliteChangeFeedHostedService(SqliteChangeFeedProcessor processor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        processor.RunAsync(stoppingToken);
}

/// <summary>
/// Hosts the SQLite event log engine as a background worker.
/// </summary>
internal sealed class SqliteEventFeedHostedService(SqliteEventFeedProcessor processor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        processor.RunAsync(stoppingToken);
}

/// <summary>
/// Purges expired events on a fixed interval — only active when at least one event type
/// declares a retention.
/// </summary>
internal sealed class SqliteEventRetentionHostedService(
    PapumaKernelLocalOptions options,
    KernelModel model,
    ILogger<SqliteEventRetentionHostedService>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteEventRetentionHostedService>.Instance;

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
                var deleted = await SqliteEventRetention.PurgeExpiredAsync(
                    options.ResolveConnectionString(), model, stoppingToken);
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
