// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Diagnostics;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// The event log engine (ADR-013): delivers committed events to registered handlers
/// with the same guarantees as the change feed engine (ADR-009/010/022) — commit
/// order via the snapshot cursor, persisted checkpoints, retry with backoff, poison
/// skipping, leader coordination via <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
/// <remarks>
/// Change feed and event log are separate feeds with separate checkpoint spaces
/// (handler names are prefixed with <c>event:</c> in <c>papuma.checkpoint</c>) —
/// there is no global ordering across the two feeds; correlate via
/// <c>correlationId</c> (ADR-013).
/// </remarks>
public sealed class EventFeedProcessor : IDisposable
{
    private const string CheckpointPrefix = "event:";
    private const string FeedTag = "event";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<IEventHandler> _handlers;
    private readonly ChangeFeedProcessorOptions _options;
    private readonly ILogger _logger;

    // Per-instance meter (same name as the shared one) so the observable lag gauge can
    // be disposed with the processor (phase 11); see ChangeFeedProcessor.
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, long> _lagByHandler = new(StringComparer.Ordinal);
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

        _meter = new Meter(KernelDiagnostics.SourceName);
        _meter.CreateObservableGauge(
            "papuma.feed.lag",
            ObserveLag,
            unit: "{record}",
            description: "Stable-visible feed head minus checkpoint, per handler. " +
                         "Refreshed by GetLagAsync and the idle moments of the run loop.");
    }

    /// <summary>Disposes the per-instance lag gauge.</summary>
    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveLag()
    {
        foreach (var (handler, lag) in _lagByHandler)
        {
            yield return new Measurement<long>(lag,
                new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                new KeyValuePair<string, object?>("papuma.handler", handler));
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
                    await GetLagAsync(ct); // gauge freshness ≈ poll interval
                    await listenConn.WaitAsync(_options.PollInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lag refresh failed; continuing.");
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

        var stopwatch = Stopwatch.StartNew();
        var total = 0;
        foreach (var handler in _handlers)
        {
            total += await ProcessHandlerBatchAsync(handler, ct);
        }

        KernelDiagnostics.CycleDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("papuma.feed", FeedTag));
        return total;
    }

    /// <summary>
    /// Returns the persisted failure entries (retrying and poison) of this processor's
    /// handlers — the failure table as an API (phase 11 diagnostics).
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct = default) =>
        PostgresFeedDiagnostics.GetFailuresAsync(_dataSource, _handlers.Select(h => h.Name), CheckpointPrefix, ct);

    /// <summary>
    /// Removes a failure entry so the next cycle retries the event immediately —
    /// the manual override after fixing a poison cause.
    /// </summary>
    /// <param name="handlerName">The handler name (without the internal prefix).</param>
    /// <param name="seq">The event log sequence number.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>true</c> when a failure entry existed and was removed.</returns>
    public Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct = default) =>
        PostgresFeedDiagnostics.RetryFailureAsync(_dataSource, handlerName, CheckpointPrefix, seq, ct);

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
            checkpointCmd.CommandText = SnapshotCursor.ResetSql;
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
        await conn.SetAllScopesAsync(tx, ct);

        long latestSeq;
        await using (var headCmd = conn.CreateCommand())
        {
            headCmd.Transaction = tx;
            headCmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM papuma.event";
            latestSeq = (long)(await headCmd.ExecuteScalarAsync(ct))!;
        }

        // Lag = committed rows not yet delivered (ADR-022), counted — a seq difference has
        // no meaning once delivery follows commit order.
        var snapshots = new List<ChangeFeedLagSnapshot>(_handlers.Count);
        foreach (var handler in _handlers)
        {
            var cursor = await SnapshotCursor.ReadOrInitialAsync(conn, tx, CheckpointPrefix + handler.Name, ct);
            var lag = await cursor.CountUndeliveredAsync(conn, tx, "papuma.event", ct);

            snapshots.Add(new ChangeFeedLagSnapshot(handler.Name, cursor.LastSeq, latestSeq, lag));
            _lagByHandler[handler.Name] = lag; // feeds the observable gauge
        }

        return snapshots;
    }

    private async Task<int> ProcessHandlerBatchAsync(IEventHandler handler, CancellationToken ct)
    {
        var key = CheckpointPrefix + handler.Name;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(tx, ct);

        // Leader coordination: skip the handler when another processor holds its checkpoint.
        var locked = await SnapshotCursor.LockAsync(conn, tx, key, ct);
        if (locked is null)
        {
            return 0; // locked by another instance (or not yet registered)
        }

        var cursor = await locked.WithSliceAsync(conn, tx, "papuma.event", ct);
        var batch = cursor.SliceSnapshot is null ? [] : await LoadBatchAsync(conn, tx, key, cursor, ct);
        if (batch.Count == 0 && locked.SliceSnapshot is not null)
        {
            // The slice in progress has run dry, so it is complete — go straight on with a
            // fresh one: a drained slice must not make the cycle look idle while newer
            // commits wait.
            cursor = await cursor.After(cursor.SliceSeq, 0, sliceCompleted: true).WithSliceAsync(conn, tx, "papuma.event", ct);
            batch = cursor.SliceSnapshot is null ? [] : await LoadBatchAsync(conn, tx, key, cursor, ct);
        }

        if (batch.Count == 0)
        {
            // Nothing to deliver. Keep a completed slice; drop an empty fresh one — the next
            // cycle takes a newer snapshot anyway, and an idle cycle then writes nothing.
            var idle = cursor with { SliceSnapshot = null, SliceSeq = 0 };
            if (idle != locked)
            {
                await idle.SaveAsync(conn, tx, key, ct);
            }

            await tx.CommitAsync(ct);
            return 0;
        }

        var processed = 0;
        var position = cursor.SliceSeq;
        var highest = 0L;
        var stopped = false;
        foreach (var item in batch)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Attempts >= _options.MaxAttempts)
            {
                _logger.LogError(
                    "Event handler {Handler} skips poison event seq {Seq} after {Attempts} attempts.",
                    handler.Name, item.Record.Seq, item.Attempts);
                KernelDiagnostics.FeedPoisoned.Add(1,
                    new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                    new KeyValuePair<string, object?>("papuma.handler", handler.Name));
                position = item.Record.Seq;
                highest = Math.Max(highest, item.Record.Seq);
                continue;
            }

            if (item.RetryPending)
            {
                stopped = true;
                break; // stop-the-line (ADR-009)
            }

            // Handler span links to the originating write's trace (metadata traceparent).
            using var activity = FeedDiagnostics.StartHandlerActivity(
                FeedTag, handler.Name, item.Record.Seq, item.Record.Metadata);
            var stopwatch = Stopwatch.StartNew();
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
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                KernelDiagnostics.FeedFailures.Add(1,
                    new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                    new KeyValuePair<string, object?>("papuma.handler", handler.Name));
                var attempts = await RegisterFailureAsync(conn, tx, key, item.Record.Seq, ex, ct);
                _logger.LogWarning(ex,
                    "Event handler {Handler} failed on event seq {Seq} (attempt {Attempts}/{MaxAttempts}).",
                    handler.Name, item.Record.Seq, attempts, _options.MaxAttempts);
                stopped = true;
                break;
            }

            KernelDiagnostics.HandlerDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                new KeyValuePair<string, object?>("papuma.handler", handler.Name));
            KernelDiagnostics.FeedProcessed.Add(1,
                new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                new KeyValuePair<string, object?>("papuma.handler", handler.Name));

            if (item.Attempts > 0)
            {
                await ClearFailureAsync(conn, tx, key, item.Record.Seq, ct);
            }

            position = item.Record.Seq;
            highest = Math.Max(highest, item.Record.Seq);
            processed++;
        }

        // A batch that came back short without stopping the line has emptied the slice.
        var sliceCompleted = !stopped && batch.Count < _options.BatchSize;
        var next = cursor.After(position, highest, sliceCompleted);
        if (next != locked)
        {
            await next.SaveAsync(conn, tx, key, ct);
        }

        await tx.CommitAsync(ct);
        return processed;
    }

    // RetryPending is decided by the database clock that also set next_retry_at — comparing it
    // with the application's clock would shift every retry by the clock skew between hosts.
    private sealed record BatchItem(EventRecord Record, int Attempts, bool RetryPending);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string checkpointKey, SnapshotCursor cursor, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // The next rows of the cursor's slice: transactions committed since the done
        // snapshot, whole, in seq order (ADR-022).
        var slice = cursor.SlicePredicate(cmd, "e");
        cmd.CommandText = $"""
            SELECT e.seq, e.scope, e.tenant_id, e.event_type, e.payload::text, e.actor_id,
                   e.metadata::text, e.occurred_at, COALESCE(f.attempts, 0),
                   COALESCE(f.next_retry_at > now(), false) AS retry_pending
            FROM papuma.event e
            LEFT JOIN papuma.failure f
                   ON f.handler_name = @name AND f.seq = e.seq
            WHERE {slice}
            ORDER BY e.seq
            LIMIT @batchSize
            """;
        cmd.Parameters.AddWithValue("name", checkpointKey);
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
                ActorId: reader.GetString(5),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(6))!,
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(7));

            batch.Add(new BatchItem(
                record,
                reader.GetInt32(8),
                reader.GetBoolean(9)));
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
        cmd.Parameters.AddWithValue("error", FeedDiagnostics.SanitizeError(ex));
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
