// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// Polls the transactional outbox and publishes pending messages with at-least-once semantics.
/// </summary>
public sealed class OutboxWorker : BackgroundService
{
    private static readonly TimeSpan MinErrorDelay = TimeSpan.FromSeconds(5);

    private readonly IOutboxPublisher _publisher;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<OutboxWorker> _logger;
    private readonly OutboxWorkerOptions _options;
    private readonly ScopeFilter _scopeFilter;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxWorker"/> class.
    /// </summary>
    /// <param name="publisher">The publisher that dispatches outbox entries.</param>
    /// <param name="dataSource">The data source used to load and update outbox rows.</param>
    /// <param name="logger">The logger used for worker diagnostics.</param>
    /// <param name="options">Optional polling and retry configuration.</param>
    /// <param name="scopeFilter">Scope filter for this worker. Use <see cref="ScopeFilter.All()"/> to process all scopes.</param>
    public OutboxWorker(
        IOutboxPublisher publisher,
        NpgsqlDataSource dataSource,
        ILogger<OutboxWorker> logger,
        OutboxWorkerOptions? options = null,
        ScopeFilter? scopeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        _publisher = publisher;
        _dataSource = dataSource;
        _logger = logger;
        _options = options ?? new OutboxWorkerOptions();
        _scopeFilter = scopeFilter ?? ScopeFilter.All();

        ValidateOptions(_options);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed < _options.BatchSize)
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
                _logger.LogError(ex, "Error in OutboxWorker");

                var errorDelay = _options.PollInterval < MinErrorDelay ? MinErrorDelay : _options.PollInterval;
                await Task.Delay(errorDelay, stoppingToken);
            }
        }

        _logger.LogInformation("OutboxWorker stopped.");
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var loadTx = await conn.BeginTransactionAsync(ct);
        if (_scopeFilter.IsAll)
            await conn.SetAllScopesAsync(ct);
        else
            await conn.SetScopeAsync(_scopeFilter.Scope!, ct);

        var entries = await LoadEntriesAsync(conn, loadTx, ct);
        await loadTx.CommitAsync(ct);

        foreach (var entry in entries)
        {
            try
            {
                var published = await _publisher.PublishAsync(entry.Scope, entry.EventId, entry.EventType, entry.PayloadJson, ct);
                if (published)
                {
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await MarkSentAsync(conn, tx, entry.OutboxId, ct);
                    await tx.CommitAsync(ct);
                    continue;
                }

                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    var deadLettered = await RegisterFailureAsync(conn, tx, entry, new InvalidOperationException("IOutboxPublisher.PublishAsync returned false."), ct);
                    await tx.CommitAsync(ct);

                    if (deadLettered)
                    {
                        _logger.LogWarning(
                            "Outbox entry {OutboxId} reached the maximum retry count and was dead-lettered.",
                            entry.OutboxId);
                    }
                }
            }
            catch (Exception ex)
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                var deadLettered = await RegisterFailureAsync(conn, tx, entry, ex, ct);
                await tx.CommitAsync(ct);

                if (deadLettered)
                {
                    _logger.LogWarning(
                        ex,
                        "Outbox entry {OutboxId} reached the maximum retry count and was dead-lettered.",
                        entry.OutboxId);
                    continue;
                }

                throw;
            }
        }

        return entries.Count;
    }

    private async Task<List<OutboxEntry>> LoadEntriesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT outbox_id, scope, tenant_id, event_id, event_type, payload::text, attempts
            FROM papuma_event_outbox
            WHERE status IN ('Pending', 'Failed')
              AND next_retry_at <= NOW()
              AND attempts < @maxAttempts
              AND (@scope IS NULL OR scope = @scope)
              AND (
                  @scope IS NULL
                  OR
                  @scope <> 'Tenant'
                  OR
                  tenant_id = @tenantId
              )
            ORDER BY outbox_id
            LIMIT @batchSize
            """;

        cmd.Parameters.AddWithValue("maxAttempts", _options.MaxAttemptsPerMessage);
        cmd.Parameters.AddWithValue("scope", (object?)_scopeFilter.Scope?.Scope.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tenantId", (object?)_scopeFilter.Scope?.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("batchSize", _options.BatchSize);

        var entries = new List<OutboxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var scope = Enum.Parse<ScopeType>(reader.GetString(1), ignoreCase: false);
            var tenantId = reader.IsDBNull(2) ? null : reader.GetString(2);

            entries.Add(new OutboxEntry(
                OutboxId: reader.GetInt64(0),
                Scope: scope == ScopeType.Platform ? ScopeContext.Platform() : ScopeContext.Tenant(tenantId!),
                EventId: reader.GetGuid(3),
                EventType: reader.GetString(4),
                PayloadJson: reader.GetString(5),
                Attempts: reader.GetInt32(6)));
        }

        return entries;
    }

    private async Task MarkSentAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long outboxId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE papuma_event_outbox
            SET status = 'Sent',
                attempts = 0,
                last_error = NULL,
                updated_at = NOW()
            WHERE outbox_id = @outboxId
            """;
        cmd.Parameters.AddWithValue("outboxId", outboxId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> RegisterFailureAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        OutboxEntry entry,
        Exception ex,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE papuma_event_outbox
            SET status = 'Failed',
                attempts = attempts + 1,
                last_error = @error,
                next_retry_at = NOW() + LEAST(
                    @baseDelay * POWER(2, attempts),
                    @maxDelay
                ) * INTERVAL '1 second',
                updated_at = NOW()
            WHERE outbox_id = @outboxId
            RETURNING attempts
            """;
        cmd.Parameters.AddWithValue("error", $"{ex.GetType().Name}: {ex.Message}");
        cmd.Parameters.AddWithValue("baseDelay", _options.BaseRetryDelay.TotalSeconds);
        cmd.Parameters.AddWithValue("maxDelay", _options.MaxRetryDelay.TotalSeconds);
        cmd.Parameters.AddWithValue("outboxId", entry.OutboxId);

        var attempts = (int)(await cmd.ExecuteScalarAsync(ct) ?? entry.Attempts + 1);
        return attempts >= _options.MaxAttemptsPerMessage;
    }

    private static void ValidateOptions(OutboxWorkerOptions options)
    {
        if (options.BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be greater than or equal to 1.");
        }

        if (options.MaxAttemptsPerMessage < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttemptsPerMessage must be greater than or equal to 1.");
        }

        if (options.PollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must not be negative.");
        }

        if (options.BaseRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BaseRetryDelay must not be negative.");
        }

        if (options.MaxRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetryDelay must not be negative.");
        }

        if (options.MaxRetryDelay < options.BaseRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetryDelay must be greater than or equal to BaseRetryDelay.");
        }
    }

    private sealed record OutboxEntry(
        long OutboxId,
        ScopeContext Scope,
        Guid EventId,
        string EventType,
        string PayloadJson,
        int Attempts);
}