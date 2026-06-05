// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Polls the change feed and applies matching events to an external projection with at-least-once semantics.
/// </summary>
public sealed class ExternalProjectionWorker : BackgroundService, IProjectionLagProvider
{
    private static readonly TimeSpan MinErrorDelay = TimeSpan.FromSeconds(5);

    private readonly IExternalProjectionHandler _handler;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ExternalProjectionWorker> _logger;
    private readonly ProjectionWorkerOptions _options;
    private readonly ScopeFilter _scopeFilter;
    private readonly string _projectionName;

    /// <summary>
    /// Gets the unique projection name used for checkpointing and failure tracking.
    /// </summary>
    public string ProjectionName => _projectionName;

    /// <summary>
    /// Gets a lag snapshot containing checkpoint and latest sequence id for this projection.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The current lag snapshot.</returns>
    public async Task<ProjectionLagSnapshot> GetLagSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        if (!_scopeFilter.IsAll)
        {
            await conn.SetScopeAsync(_scopeFilter.Scope!, ct);
        }

        var checkpoint = await LoadCheckpointAsync(conn, ct);
        var latestSequenceId = await LoadLatestRelevantSequenceIdAsync(conn, ct);
        var lag = latestSequenceId > checkpoint ? latestSequenceId - checkpoint : 0L;

        return new ProjectionLagSnapshot(_projectionName, checkpoint, latestSequenceId, lag);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalProjectionWorker"/> class.
    /// </summary>
    /// <param name="handler">The external projection handler that applies change feed records.</param>
    /// <param name="dataSource">The data source used to load and checkpoint records.</param>
    /// <param name="logger">The logger used for worker diagnostics.</param>
    /// <param name="options">Optional polling and retry configuration.</param>
    /// <param name="scopeFilter">Scope filter for this worker. Use <see cref="ScopeFilter.All()"/> to process all scopes.</param>
    public ExternalProjectionWorker(
        IExternalProjectionHandler handler,
        NpgsqlDataSource dataSource,
        ILogger<ExternalProjectionWorker> logger,
        ProjectionWorkerOptions? options = null,
        ScopeFilter? scopeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        _handler = handler;
        _dataSource = dataSource;
        _logger = logger;
        _options = options ?? new ProjectionWorkerOptions();
        _scopeFilter = scopeFilter ?? ScopeFilter.All();
        _projectionName = _scopeFilter.IsAll
            ? handler.Name
            : _scopeFilter.Scope!.Scope == ScopeType.Platform
                ? $"{handler.Name}@Platform"
                : $"{handler.Name}@Tenant:{_scopeFilter.Scope!.TenantId}";

        ValidateOptions(_options);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExternalProjectionWorker [{Name}] started.", _projectionName);

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
                _logger.LogError(ex, "Error in ExternalProjectionWorker [{Name}]", _projectionName);

                var errorDelay = _options.PollInterval < MinErrorDelay ? MinErrorDelay : _options.PollInterval;
                await Task.Delay(errorDelay, stoppingToken);
            }
        }

        _logger.LogInformation("ExternalProjectionWorker [{Name}] stopped.", _projectionName);
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        if (!_scopeFilter.IsAll)
        {
            await conn.SetScopeAsync(_scopeFilter.Scope!, ct);
        }

        var checkpoint = await LoadCheckpointAsync(conn, ct);
        var changes = await LoadChangesAsync(conn, checkpoint, ct);

        foreach (var change in changes)
        {
            try
            {
                await _handler.HandleAsync(change, ct);

                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    await ClearFailureAsync(conn, tx, change.SequenceId, ct);
                    await SaveCheckpointAsync(conn, tx, change.SequenceId, ct);
                    await tx.CommitAsync(ct);
                }
            }
            catch (Exception ex)
            {
                var movedToDeadLetter = await RegisterFailureAsync(conn, change, ex, ct);
                if (movedToDeadLetter)
                {
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await SaveCheckpointAsync(conn, tx, change.SequenceId, ct);
                    await tx.CommitAsync(ct);

                    _logger.LogWarning(
                        ex,
                        "External projection entry {SequenceId} reached the maximum retry count and was dead-lettered.",
                        change.SequenceId);

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
            SELECT sequence_id, scope, tenant_id, entity, entity_id, event_type, version,
                   correlation_id, causation_id, actor_id, payload::text, timestamp
            FROM change_feed
            WHERE sequence_id > @lastSeen
              AND (@scope IS NULL OR scope = @scope)
              AND (
                  @scope IS NULL
                  OR
                  @scope <> 'Tenant'
                  OR
                  tenant_id = @tenantId
              )
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
        cmd.Parameters.AddWithValue("scope", (object?)_scopeFilter.Scope?.Scope.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tenantId", (object?)_scopeFilter.Scope?.TenantId ?? DBNull.Value);
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
                Entity: reader.GetString(3),
                EntityId: reader.GetString(4),
                EventType: reader.GetString(5),
                Version: reader.GetInt32(6),
                CorrelationId: reader.IsDBNull(7) ? null : reader.GetString(7),
                CausationId: reader.IsDBNull(8) ? null : reader.GetString(8),
                ActorId: reader.GetString(9),
                PayloadJson: reader.GetString(10),
                Timestamp: reader.GetFieldValue<DateTimeOffset>(11),
                Scope: Enum.Parse<ScopeType>(reader.GetString(1), ignoreCase: false),
                TenantId: reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return records;
    }

    private async Task<long> LoadLatestRelevantSequenceIdAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(sequence_id), 0)
            FROM change_feed
            WHERE (@scope IS NULL OR scope = @scope)
              AND (
                  @scope IS NULL
                  OR
                  @scope <> 'Tenant'
                  OR
                  tenant_id = @tenantId
              )
              AND redacted = FALSE
              AND event_type = ANY(@eventTypes)
              AND xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
            """;

        cmd.Parameters.AddWithValue("scope", (object?)_scopeFilter.Scope?.Scope.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tenantId", (object?)_scopeFilter.Scope?.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("eventTypes", _handler.EventTypes.ToArray());

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long latestSequenceId ? latestSequenceId : 0L;
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
}