// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Deletes redacted records older than the configured retention window from the unified event feed.
/// </summary>
public sealed class RetentionWorker : BackgroundService
{
    private static readonly TimeSpan MinErrorDelay = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<RetentionWorker> _logger;
    private readonly RetentionWorkerOptions _options;
    private readonly ScopeFilter _scopeFilter;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionWorker"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="options">Optional worker configuration.</param>
    /// <param name="scopeFilter">Optional scope filter to limit retention to a specific scope.</param>
    public RetentionWorker(
        NpgsqlDataSource dataSource,
        ILogger<RetentionWorker> logger,
        RetentionWorkerOptions? options = null,
        ScopeFilter? scopeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _logger = logger;
        _options = options ?? new RetentionWorkerOptions();
        _scopeFilter = scopeFilter ?? ScopeFilter.All();

        ValidateOptions(_options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RetentionWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await RunCleanupCycleAsync(stoppingToken);
                if (deleted == 0)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RetentionWorker");
                var errorDelay = _options.PollInterval < MinErrorDelay ? MinErrorDelay : _options.PollInterval;
                await Task.Delay(errorDelay, stoppingToken);
            }
        }

        _logger.LogInformation("RetentionWorker stopped.");
    }

    /// <summary>
    /// Deletes redacted rows older than the cutoff timestamp, limited to the configured batch size.
    /// </summary>
    private async Task<int> RunCleanupCycleAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - _options.RetentionWindow;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!_scopeFilter.IsAll)
        {
            await conn.SetScopeAsync(_scopeFilter.Scope!, ct);
        }
        else
        {
            await conn.SetAllScopesAsync(ct);
        }

        var deleted = await DeleteRedactedRecordsAsync(conn, tx, cutoff, ct);

        await tx.CommitAsync(ct);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "RetentionWorker deleted {Deleted} papuma_event_feed rows older than {Cutoff}.",
                deleted,
                cutoff);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes redacted <c>papuma_event_feed</c> rows older than the cutoff using a CTE-based DELETE.
    /// </summary>
    private async Task<int> DeleteRedactedRecordsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            WITH candidates AS (
                SELECT sequence_id
                FROM papuma_event_feed
                WHERE redacted = TRUE
                  AND occurred_at < @cutoff
                  AND (@scope IS NULL OR scope = @scope)
                  AND (
                      @scope IS NULL
                      OR
                      @scope <> 'Tenant'
                      OR
                      tenant_id = @tenantId
                  )
                ORDER BY sequence_id
                LIMIT @batchSize
            )
            DELETE FROM papuma_event_feed ef
            USING candidates
            WHERE ef.sequence_id = candidates.sequence_id
            """;

        cmd.Parameters.AddWithValue("cutoff", cutoff);
        cmd.Parameters.AddWithValue("scope", (object?)_scopeFilter.Scope?.Scope.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tenantId", (object?)_scopeFilter.Scope?.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("batchSize", _options.BatchSize);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Validates that the configured options fall within acceptable ranges.
    /// </summary>
    private static void ValidateOptions(RetentionWorkerOptions options)
    {
        if (options.BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be greater than or equal to 1.");
        }

        if (options.PollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must not be negative.");
        }

        if (options.RetentionWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetentionWindow must be greater than zero.");
        }
    }
}
