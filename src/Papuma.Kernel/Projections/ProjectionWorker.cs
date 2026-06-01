// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Polls the change feed and applies matching events to a projection with at-least-once semantics.
/// </summary>
public sealed class ProjectionWorker : BackgroundService
{
    private static readonly TimeSpan MinErrorDelay = TimeSpan.FromSeconds(5);

    private readonly IProjectionHandler _handler;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ProjectionWorker> _logger;
    private readonly ProjectionWorkerOptions _options;
    private readonly TenantContext? _tenant;
    private readonly string _projectionName;
    private readonly Channel<ReplayRequest> _replayChannel = Channel.CreateBounded<ReplayRequest>(1);

    /// <summary>
    /// Gets the unique projection name used for checkpointing and failure tracking.
    /// </summary>
    public string ProjectionName => _projectionName;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectionWorker"/> class.
    /// </summary>
    /// <param name="handler">The projection handler that applies change feed records.</param>
    /// <param name="dataSource">The data source used to load and checkpoint records.</param>
    /// <param name="logger">The logger used for worker diagnostics.</param>
    /// <param name="options">Optional polling and retry configuration.</param>
    /// <param name="tenant">Optional tenant scope for this worker.</param>
    public ProjectionWorker(
        IProjectionHandler handler,
        NpgsqlDataSource dataSource,
        ILogger<ProjectionWorker> logger,
        ProjectionWorkerOptions? options = null,
        TenantContext? tenant = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        _handler = handler;
        _dataSource = dataSource;
        _logger = logger;
        _options = options ?? new ProjectionWorkerOptions();
        _tenant = tenant;
        _projectionName = tenant is null ? handler.Name : $"{handler.Name}@{tenant.TenantId}";

        ValidateOptions(_options);
    }

    /// <summary>
    /// Requests a full replay for this projection worker.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async Task RequestReplayAsync(CancellationToken ct = default)
    {
        await _replayChannel.Writer.WriteAsync(new ReplayRequest(), ct);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProjectionWorker [{Name}] started.", _projectionName);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_replayChannel.Reader.TryRead(out _))
            {
                _logger.LogInformation(
                    "ProjectionWorker [{Name}] replay requested, resetting projection state.",
                    _projectionName);

                await ResetProjectionStateAsync(stoppingToken);

                if (_handler is IReplayableProjection replayable)
                {
                    await replayable.PrepareReplayAsync(stoppingToken);
                }
            }

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
                _logger.LogError(ex, "Error in ProjectionWorker [{Name}]", _projectionName);

                // Use at least 5 seconds to avoid log flooding when the database is unreachable.
                var errorDelay = _options.PollInterval < MinErrorDelay ? MinErrorDelay : _options.PollInterval;
                await Task.Delay(errorDelay, stoppingToken);
            }
        }

        _logger.LogInformation("ProjectionWorker [{Name}] stopped.", _projectionName);
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var checkpoint = await LoadCheckpointAsync(conn, ct);
        var changes = await LoadChangesAsync(conn, checkpoint, ct);

        foreach (var change in changes)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await _handler.HandleAsync(change, conn, tx, ct);
                await ClearFailureAsync(conn, tx, change.SequenceId, ct);
                await SaveCheckpointAsync(conn, tx, change.SequenceId, ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);

                // RegisterFailureAsync runs outside the rolled-back transaction.
                // If the worker crashes between here and the next loop iteration the
                // failure row is persisted but the checkpoint is not advanced.  On
                // restart the event will be reloaded and the attempts counter will be
                // incremented again – this is correct at-least-once behaviour.
                var movedToDeadLetter = await RegisterFailureAsync(conn, change, ex, ct);
                if (movedToDeadLetter)
                {
                    await using var skipTx = await conn.BeginTransactionAsync(ct);
                    await SaveCheckpointAsync(conn, skipTx, change.SequenceId, ct);
                    await skipTx.CommitAsync(ct);
                    continue;
                }

                throw;
            }
        }

        return changes.Count;
    }

    private async Task<long> LoadCheckpointAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT last_sequence_id
            FROM projection_checkpoint
            WHERE projection_name = @name
            """;
        cmd.Parameters.AddWithValue("name", _projectionName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long id ? id : 0L;
    }

    private async Task<List<ChangeRecord>> LoadChangesAsync(
        NpgsqlConnection conn,
        long fromSequenceId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sequence_id, tenant_id, entity, entity_id, event_type, version,
                   correlation_id, causation_id, actor_id, payload::text, timestamp
            FROM change_feed
            WHERE sequence_id > @lastSeen
              AND (@tenantId IS NULL OR tenant_id = @tenantId)
              AND redacted = FALSE
              AND event_type = ANY(@eventTypes)
              AND xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
              AND NOT EXISTS (
                  SELECT 1
                  FROM projection_failures pf
                  WHERE pf.projection_name = @name
                    AND pf.sequence_id = change_feed.sequence_id
                    AND (
                        pf.attempts >= @maxAttempts
                        OR pf.next_retry_at > NOW())
              )
            ORDER BY sequence_id
            LIMIT @batchSize
            """;

        cmd.Parameters.AddWithValue("lastSeen", fromSequenceId);
        cmd.Parameters.AddWithValue("tenantId", (object?)_tenant?.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", _projectionName);
        cmd.Parameters.AddWithValue("maxAttempts", _options.MaxAttemptsPerEvent);
        cmd.Parameters.AddWithValue("eventTypes", _handler.EventTypes.ToArray());
        cmd.Parameters.AddWithValue("batchSize", _options.BatchSize);

        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                SequenceId: reader.GetInt64(0),
                Entity: reader.GetString(2),
                EntityId: reader.GetString(3),
                EventType: reader.GetString(4),
                Version: reader.GetInt32(5),
                CorrelationId: reader.IsDBNull(6) ? null : reader.GetString(6),
                CausationId: reader.IsDBNull(7) ? null : reader.GetString(7),
                ActorId: reader.GetString(8),
                PayloadJson: reader.GetString(9),
                Timestamp: reader.GetFieldValue<DateTimeOffset>(10),
                TenantId: reader.GetString(1)));
        }

        return records;
    }

    private async Task SaveCheckpointAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long sequenceId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projection_checkpoint (projection_name, last_sequence_id, updated_at)
            VALUES (@name, @sequenceId, NOW())
            ON CONFLICT (projection_name)
            DO UPDATE SET last_sequence_id = @sequenceId, updated_at = NOW()
            """;
        cmd.Parameters.AddWithValue("name", _projectionName);
        cmd.Parameters.AddWithValue("sequenceId", sequenceId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ClearFailureAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long sequenceId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM projection_failures
            WHERE projection_name = @name
              AND sequence_id = @sequenceId
            """;
        cmd.Parameters.AddWithValue("name", _projectionName);
        cmd.Parameters.AddWithValue("sequenceId", sequenceId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> RegisterFailureAsync(
        NpgsqlConnection conn,
        ChangeRecord change,
        Exception ex,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projection_failures
                (projection_name, sequence_id, event_type, attempts, last_error, next_retry_at)
            VALUES
                (@name, @sequenceId, @eventType, 1, @error,
                 NOW() + LEAST(
                     @baseDelay * POWER(2, 0),
                     @maxDelay
                 ) * INTERVAL '1 second')
            ON CONFLICT (projection_name, sequence_id)
            DO UPDATE SET
                attempts = projection_failures.attempts + 1,
                last_error = EXCLUDED.last_error,
                next_retry_at = NOW() + LEAST(
                    @baseDelay * POWER(2, projection_failures.attempts),
                    @maxDelay
                ) * INTERVAL '1 second',
                updated_at = NOW()
            RETURNING attempts
            """;

        cmd.Parameters.AddWithValue("name", _projectionName);
        cmd.Parameters.AddWithValue("sequenceId", change.SequenceId);
        cmd.Parameters.AddWithValue("eventType", change.EventType);
        cmd.Parameters.AddWithValue("error", ex.ToString());
        cmd.Parameters.AddWithValue("baseDelay", _options.BaseRetryDelay.TotalSeconds);
        cmd.Parameters.AddWithValue("maxDelay", _options.MaxRetryDelay.TotalSeconds);

        var attempts = (int)(await cmd.ExecuteScalarAsync(ct) ?? 1);
        return attempts >= _options.MaxAttemptsPerEvent;
    }

    private async Task ResetProjectionStateAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var checkpointCmd = conn.CreateCommand())
        {
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = """
                INSERT INTO projection_checkpoint (projection_name, last_sequence_id, updated_at)
                VALUES (@name, 0, NOW())
                ON CONFLICT (projection_name)
                DO UPDATE SET last_sequence_id = 0, updated_at = NOW()
                """;
            checkpointCmd.Parameters.AddWithValue("name", _projectionName);
            await checkpointCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var failuresCmd = conn.CreateCommand())
        {
            failuresCmd.Transaction = tx;
            failuresCmd.CommandText = """
                DELETE FROM projection_failures
                WHERE projection_name = @name
                """;
            failuresCmd.Parameters.AddWithValue("name", _projectionName);
            await failuresCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static void ValidateOptions(ProjectionWorkerOptions options)
    {
        if (options.BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be greater than or equal to 1.");
        }

        if (options.MaxAttemptsPerEvent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttemptsPerEvent must be greater than or equal to 1.");
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

    private sealed record ReplayRequest;
}
