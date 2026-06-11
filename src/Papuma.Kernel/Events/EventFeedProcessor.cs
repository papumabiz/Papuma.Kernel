// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Processing;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// The event log engine (ADR-013): delivers committed events to registered handlers
/// with the same guarantees as the change feed engine (ADR-009/010) — strict
/// <c>seq</c> order, persisted checkpoints, retry with backoff, poison skipping,
/// snapshot-stable reads, leader coordination via <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
/// <remarks>
/// Change feed and event log are separate feeds with separate checkpoint spaces
/// (handler names are prefixed with <c>event:</c> in <c>papuma.checkpoint</c>) —
/// there is no global ordering across the two feeds; correlate via
/// <c>correlationId</c> (ADR-013).
/// </remarks>
public sealed class EventFeedProcessor
{
    private const string CheckpointPrefix = "event:";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<IEventHandler> _handlers;
    private readonly ChangeFeedProcessorOptions _options;
    private readonly ILogger _logger;
    private bool _registered;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventFeedProcessor"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="handlers">The event handlers (unique names).</param>
    /// <param name="options">Engine options (optional; shared shape with the change feed engine).</param>
    /// <param name="logger">Logger (optional).</param>
    public EventFeedProcessor(
        NpgsqlDataSource dataSource,
        IEnumerable<IEventHandler> handlers,
        ChangeFeedProcessorOptions? options = null,
        ILogger<EventFeedProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(handlers);

        _dataSource = dataSource;
        _handlers = handlers.ToList();
        _options = options ?? new ChangeFeedProcessorOptions();
        _options.Validate();
        _logger = logger ?? NullLogger<EventFeedProcessor>.Instance;

        var duplicate = _handlers.GroupBy(h => h.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Handler name '{duplicate.Key}' is registered more than once.", nameof(handlers));
        }
    }

    /// <summary>
    /// Runs the processing loop until cancellation (NOTIFY wakeup, polling as truth — ADR-010).
    /// </summary>
    /// <param name="ct">The cancellation token stopping the loop.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_handlers.Count == 0)
        {
            _logger.LogInformation("Event feed processor idle — no handlers registered.");
            return;
        }

        await using var listenConn = await _dataSource.OpenConnectionAsync(ct);
        await using (var listenCmd = listenConn.CreateCommand())
        {
            listenCmd.CommandText = $"LISTEN {ChangeFeedProcessor.NotifyChannel}";
            await listenCmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Event feed processor started ({HandlerCount} handlers).", _handlers.Count);

        while (!ct.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await ProcessOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event feed processing cycle failed.");
                processed = 0;
            }

            if (processed == 0)
            {
                try
                {
                    await listenConn.WaitAsync(_options.PollInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Event feed processor stopped.");
    }

    /// <summary>
    /// Runs one processing cycle over all handlers and returns the number of delivered events.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async Task<int> ProcessOnceAsync(CancellationToken ct = default)
    {
        await EnsureRegisteredAsync(ct);

        var total = 0;
        foreach (var handler in _handlers)
        {
            total += await ProcessHandlerBatchAsync(handler, ct);
        }

        return total;
    }

    /// <summary>
    /// Resets a handler's checkpoint to 0 and clears its failure entries — full replay
    /// on the next cycle.
    /// </summary>
    /// <param name="handlerName">The handler name (without the internal prefix).</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        var key = CheckpointPrefix + handlerName;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var checkpointCmd = conn.CreateCommand())
        {
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = """
                INSERT INTO papuma.checkpoint (handler_name, last_seq, updated_at)
                VALUES (@name, 0, now())
                ON CONFLICT (handler_name) DO UPDATE SET last_seq = 0, updated_at = now()
                """;
            checkpointCmd.Parameters.AddWithValue("name", key);
            await checkpointCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var failureCmd = conn.CreateCommand())
        {
            failureCmd.Transaction = tx;
            failureCmd.CommandText = "DELETE FROM papuma.failure WHERE handler_name = @name";
            failureCmd.Parameters.AddWithValue("name", key);
            await failureCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Returns a lag snapshot per handler (event log head vs. checkpoint).
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct = default)
    {
        await EnsureRegisteredAsync(ct);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(ct);

        long latestSeq;
        await using (var headCmd = conn.CreateCommand())
        {
            headCmd.Transaction = tx;
            headCmd.CommandText = """
                SELECT COALESCE(MAX(seq), 0)
                FROM papuma.event
                WHERE txid < pg_snapshot_xmin(pg_current_snapshot())
                """;
            latestSeq = (long)(await headCmd.ExecuteScalarAsync(ct))!;
        }

        var snapshots = new List<ChangeFeedLagSnapshot>(_handlers.Count);
        foreach (var handler in _handlers)
        {
            await using var checkpointCmd = conn.CreateCommand();
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = "SELECT last_seq FROM papuma.checkpoint WHERE handler_name = @name";
            checkpointCmd.Parameters.AddWithValue("name", CheckpointPrefix + handler.Name);
            var checkpoint = await checkpointCmd.ExecuteScalarAsync(ct) is long seq ? seq : 0L;

            var lag = latestSeq > checkpoint ? latestSeq - checkpoint : 0L;
            snapshots.Add(new ChangeFeedLagSnapshot(handler.Name, checkpoint, latestSeq, lag));
        }

        return snapshots;
    }

    private async Task<int> ProcessHandlerBatchAsync(IEventHandler handler, CancellationToken ct)
    {
        var key = CheckpointPrefix + handler.Name;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(ct);

        long? checkpoint;
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = """
                SELECT last_seq FROM papuma.checkpoint
                WHERE handler_name = @name
                FOR UPDATE SKIP LOCKED
                """;
            lockCmd.Parameters.AddWithValue("name", key);
            checkpoint = await lockCmd.ExecuteScalarAsync(ct) as long?;
        }

        if (checkpoint is null)
        {
            return 0; // locked by another instance (or not yet registered)
        }

        var batch = await LoadBatchAsync(conn, tx, key, checkpoint.Value, ct);
        if (batch.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var processed = 0;
        var newCheckpoint = checkpoint.Value;
        foreach (var item in batch)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Attempts >= _options.MaxAttempts)
            {
                _logger.LogError(
                    "Event handler {Handler} skips poison event seq {Seq} after {Attempts} attempts.",
                    handler.Name, item.Record.Seq, item.Attempts);
                newCheckpoint = item.Record.Seq;
                continue;
            }

            if (item.NextRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
            {
                break; // stop-the-line (ADR-009)
            }

            try
            {
                await handler.HandleAsync(item.Record, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var attempts = await RegisterFailureAsync(conn, tx, key, item.Record.Seq, ex, ct);
                _logger.LogWarning(ex,
                    "Event handler {Handler} failed on event seq {Seq} (attempt {Attempts}/{MaxAttempts}).",
                    handler.Name, item.Record.Seq, attempts, _options.MaxAttempts);
                break;
            }

            if (item.Attempts > 0)
            {
                await ClearFailureAsync(conn, tx, key, item.Record.Seq, ct);
            }

            newCheckpoint = item.Record.Seq;
            processed++;
        }

        if (newCheckpoint != checkpoint.Value)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE papuma.checkpoint
                SET last_seq = @seq, updated_at = now()
                WHERE handler_name = @name
                """;
            cmd.Parameters.AddWithValue("name", key);
            cmd.Parameters.AddWithValue("seq", newCheckpoint);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return processed;
    }

    private sealed record BatchItem(EventRecord Record, int Attempts, DateTimeOffset? NextRetryAt);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string checkpointKey, long checkpoint, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.seq, e.scope, e.tenant_id, e.event_type, e.payload::text, e.metadata::text,
                   e.occurred_at, COALESCE(f.attempts, 0), f.next_retry_at
            FROM papuma.event e
            LEFT JOIN papuma.failure f
                   ON f.handler_name = @name AND f.seq = e.seq
            WHERE e.seq > @checkpoint
              AND e.txid < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY e.seq
            LIMIT @batchSize
            """;
        cmd.Parameters.AddWithValue("name", checkpointKey);
        cmd.Parameters.AddWithValue("checkpoint", checkpoint);
        cmd.Parameters.AddWithValue("batchSize", _options.BatchSize);

        var batch = new List<BatchItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var scopeKind = reader.GetString(1);
            var scope = scopeKind == nameof(ScopeType.Tenant)
                ? ScopeContext.Tenant(reader.GetString(2))
                : ScopeContext.Platform();

            var record = new EventRecord(
                Seq: reader.GetInt64(0),
                Scope: scope,
                EventType: reader.GetString(3),
                Payload: (JsonObject)JsonNode.Parse(reader.GetString(4))!,
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(5))!,
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(6));

            batch.Add(new BatchItem(
                record,
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return batch;
    }

    private async Task<int> RegisterFailureAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string checkpointKey, long seq, Exception ex, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO papuma.failure (handler_name, seq, attempts, last_error, next_retry_at)
            VALUES (@name, @seq, 1, @error,
                    now() + LEAST(@baseDelay, @maxDelay) * INTERVAL '1 second')
            ON CONFLICT (handler_name, seq)
            DO UPDATE SET
                attempts = papuma.failure.attempts + 1,
                last_error = EXCLUDED.last_error,
                next_retry_at = now() + LEAST(
                    @baseDelay * POWER(2, papuma.failure.attempts),
                    @maxDelay) * INTERVAL '1 second',
                updated_at = now()
            RETURNING attempts
            """;
        cmd.Parameters.AddWithValue("name", checkpointKey);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("error", $"{ex.GetType().Name}: {ex.Message}");
        cmd.Parameters.AddWithValue("baseDelay", _options.BaseRetryDelay.TotalSeconds);
        cmd.Parameters.AddWithValue("maxDelay", _options.MaxRetryDelay.TotalSeconds);

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ClearFailureAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string checkpointKey, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM papuma.failure WHERE handler_name = @name AND seq = @seq";
        cmd.Parameters.AddWithValue("name", checkpointKey);
        cmd.Parameters.AddWithValue("seq", seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        if (_registered)
        {
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var handler in _handlers)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO papuma.checkpoint (handler_name, last_seq)
                VALUES (@name, 0)
                ON CONFLICT (handler_name) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("name", CheckpointPrefix + handler.Name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _registered = true;
    }
}
