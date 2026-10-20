// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Diagnostics;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Processing;

/// <summary>
/// The delivery engine behind the PostgreSQL change feed and event feed processors
/// (ADR-009/010/013/022): snapshot-cursor reads, leader coordination via
/// <c>FOR UPDATE SKIP LOCKED</c>, persisted checkpoints, retry with backoff, poison
/// skipping, projection lifecycle, lag and failure diagnostics. The two feeds differ only
/// in what a subclass supplies — table, checkpoint-key prefix, the record's columns and
/// how a row becomes a record; the public processors are thin facades over a subclass, so
/// a fix lands once.
/// </summary>
/// <typeparam name="THandler">The feed's handler interface.</typeparam>
/// <typeparam name="TRecord">The record a handler receives.</typeparam>
internal abstract class PostgresFeedEngine<THandler, TRecord> : IDisposable
    where THandler : class
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<THandler> _handlers;
    private readonly ChangeFeedProcessorOptions _options;
    private readonly ILogger _logger;

    // Per-instance meter (same name as the shared one — listeners match by name) so the
    // observable lag gauge can be disposed with the processor (phase 11). Gauge values
    // come from a cache refreshed by GetLagAsync and the idle moments of RunAsync.
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, long> _lagByHandler = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _projectionVersions;
    private readonly ConcurrentDictionary<string, bool> _pauseLogged = new(StringComparer.Ordinal);
    private bool _registered;

    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="handlers">The feed's handlers.</param>
    /// <param name="options">Engine options (optional).</param>
    /// <param name="logger">The processor's logger.</param>
    /// <param name="handlersParamName">Parameter name reported for an invalid handler list.</param>
    /// <exception cref="ArgumentException">A handler name is missing, invalid or used twice.</exception>
    protected PostgresFeedEngine(
        NpgsqlDataSource dataSource,
        IEnumerable<THandler> handlers,
        ChangeFeedProcessorOptions? options,
        ILogger logger,
        string handlersParamName)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(handlers);

        _dataSource = dataSource;
        _handlers = handlers.ToList();
        _options = options ?? new ChangeFeedProcessorOptions();
        _options.Validate();
        _logger = logger;

        foreach (var handler in _handlers)
        {
            FeedHandlerNames.Validate(NameOf(handler), handler.GetType(), handlersParamName);
        }

        var duplicate = _handlers.GroupBy(NameOf, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Handler name '{duplicate.Key}' is registered more than once.", handlersParamName);
        }

        _projectionVersions = _handlers.ToDictionary(
            NameOf, h => HandlerKind.Validate(h, NameOf(h)), StringComparer.Ordinal);

        _meter = new Meter(KernelDiagnostics.SourceName);
        _meter.CreateObservableGauge(
            "papuma.feed.lag",
            ObserveLag,
            unit: "{record}",
            description: "Committed records not yet delivered, per handler (ADR-022). " +
                         "Refreshed by GetLagAsync and the idle moments of the run loop.");
    }

    // ── What a feed supplies ─────────────────────────────────────────────────────

    /// <summary>The metric tag and the noun in log messages: <c>change</c> or <c>event</c>.</summary>
    protected abstract string FeedTag { get; }

    /// <summary>The feed's name in log messages: <c>Change</c> or <c>Event</c>.</summary>
    protected abstract string FeedName { get; }

    /// <summary>The feed table, e.g. <c>papuma.change</c>.</summary>
    protected abstract string Table { get; }

    /// <summary>
    /// The prefix of the handler's key in <c>papuma.checkpoint</c> and <c>papuma.failure</c>
    /// (shared tables, keyed by name only): empty for the change feed, <c>event:</c> for the
    /// event feed.
    /// </summary>
    protected abstract string CheckpointPrefix { get; }

    /// <summary>The table alias used in <see cref="RecordColumns"/>.</summary>
    protected abstract string Alias { get; }

    /// <summary>
    /// The select list of a record: <see cref="RecordColumnCount"/> columns, <c>seq</c>
    /// first, qualified with <see cref="Alias"/>.
    /// </summary>
    protected abstract string RecordColumns { get; }

    /// <summary>The number of columns in <see cref="RecordColumns"/>.</summary>
    protected abstract int RecordColumnCount { get; }

    protected abstract string NameOf(THandler handler);

    protected abstract Task HandleAsync(THandler handler, TRecord record, CancellationToken ct);

    protected abstract long SeqOf(TRecord record);

    protected abstract JsonObject MetadataOf(TRecord record);

    /// <summary>Maps the current row (the first <see cref="RecordColumnCount"/> columns) to a record.</summary>
    protected abstract TRecord Map(NpgsqlDataReader reader);

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    /// <summary>Disposes the per-instance lag gauge.</summary>
    public void Dispose() => _meter.Dispose();

    /// <summary>Gets a value indicating whether there is anything for <see cref="RunAsync"/> to run.</summary>
    public bool HasHandlers => _handlers.Count > 0;

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
    /// Runs the processing loop until cancellation: process all handlers, then wait for
    /// a NOTIFY wakeup or the poll interval — polling stays the source of truth (ADR-010).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_handlers.Count == 0)
        {
            _logger.LogInformation("{Feed} feed processor idle — no handlers registered.", FeedName);
            return;
        }

        await using var listen = new ListenConnection(_dataSource, _logger);
        await listen.TryOpenAsync(ct);

        _logger.LogInformation("{Feed} feed processor started ({HandlerCount} handlers).", FeedName, _handlers.Count);

        var faultedCycles = 0;
        while (!ct.IsCancellationRequested)
        {
            FeedCycleResult cycle;
            try
            {
                cycle = await RunCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // shutdown — and only shutdown: any other OCE is a fault, never a stop
            }
            catch (Exception ex)
            {
                cycle = FeedCycleResult.Failed(ex);
            }

            if (cycle.HasFaults)
            {
                faultedCycles++;
                LogFaults(cycle.Faults, faultedCycles);
            }
            else
            {
                faultedCycles = 0;
            }

            if (cycle.Processed > 0)
            {
                continue; // more may be waiting
            }

            try
            {
                if (cycle.HasFaults)
                {
                    // Never retry a failing cycle at once: a database that is down must not be
                    // answered with a connect storm and a log flood.
                    await Task.Delay(FeedLoop.BackoffDelay(_options.PollInterval, faultedCycles), ct);
                }
                else
                {
                    await TryRefreshLagCacheAsync(ct); // gauge freshness ≈ poll interval
                    // Returns early on NOTIFY; otherwise the poll interval elapses.
                    await listen.WaitAsync(_options.PollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("{Feed} feed processor stopped.", FeedName);
    }

    private void LogFaults(IReadOnlyList<FeedFault> faults, int consecutiveFaultedCycles)
    {
        foreach (var fault in faults)
        {
            if (FeedLoop.ShouldLog(consecutiveFaultedCycles))
            {
                _logger.LogError(fault.Error,
                    "{Feed} feed processing failed for {Handler} (cycle {Cycle} in a row); backing off.",
                    FeedName, fault.Handler ?? "<all handlers>", consecutiveFaultedCycles);
            }
            else
            {
                _logger.LogDebug(fault.Error,
                    "{Feed} feed processing still failing for {Handler} (cycle {Cycle} in a row).",
                    FeedName, fault.Handler ?? "<all handlers>", consecutiveFaultedCycles);
            }
        }
    }

    private async Task TryRefreshLagCacheAsync(CancellationToken ct)
    {
        try
        {
            await GetLagAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Lag refresh failed; continuing.");
        }
    }

    /// <summary>Runs one processing cycle over all handlers and returns the number of delivered records.</summary>
    /// <exception cref="AggregateException">More than one handler's batch faulted (the others still ran).</exception>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        var cycle = await RunCycleAsync(ct);
        FeedLoop.ThrowIfFaulted(cycle.Faults);
        return cycle.Processed;
    }

    // A fault in one handler's batch (an unmappable row, a failed checkpoint write) must not
    // keep the handlers after it from running: each one is isolated, the faults are reported.
    private async Task<FeedCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);

        var stopwatch = Stopwatch.StartNew();
        var total = 0;
        List<FeedFault>? faults = null;
        foreach (var handler in _handlers)
        {
            try
            {
                total += await ProcessHandlerBatchAsync(handler, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                (faults ??= []).Add(new FeedFault(NameOf(handler), ex));
            }
        }

        KernelDiagnostics.CycleDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("papuma.feed", FeedTag));
        return new FeedCycleResult(total, faults ?? []);
    }

    // ── Administration ───────────────────────────────────────────────────────────

    /// <summary>Resets a handler's checkpoint to 0 and clears its failure entries (the raw rebuild tool).</summary>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor.</exception>
    public async Task ResetCheckpointAsync(string handlerName, CancellationToken ct)
    {
        EnsureKnownHandler(handlerName);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ProjectionLifecycle.ResetCursorAsync(conn, tx, CheckpointPrefix + handlerName, version: null, ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation(
            "{Feed} feed: checkpoint for handler {Handler} reset — full replay on next cycle.", FeedName, handlerName);
    }

    /// <summary>Rebuilds every projection this processor runs; returns the number reset.</summary>
    public async Task<int> ResetProjectionsAsync(CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);

        var reset = 0;
        foreach (var handler in _handlers)
        {
            if (handler is IProjection && await ResetProjectionAsync(NameOf(handler), ct))
            {
                reset++;
            }
        }

        return reset;
    }

    /// <summary>Rebuilds one projection; <c>false</c> when a newer instance owns it.</summary>
    /// <exception cref="ArgumentException">No handler of that name, or it is not a projection.</exception>
    public async Task<bool> ResetProjectionAsync(string handlerName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        var handler = _handlers.FirstOrDefault(h => NameOf(h) == handlerName)
            ?? throw new ArgumentException($"No handler named {handlerName} runs in this processor.", nameof(handlerName));
        if (handler is not IProjection projection || _projectionVersions[handlerName] is not int declared)
        {
            throw new ArgumentException(
                $"Handler {handlerName} is not a projection (IProjection) — resetting it would repeat its effects.",
                nameof(handlerName));
        }

        await EnsureRegisteredAsync(ct);
        return await ProjectionLifecycle.ResetProjectionAsync(
            _dataSource, CheckpointPrefix + handlerName, projection, declared, _logger, ct);
    }

    /// <summary>
    /// A pure read of the lag per handler — it neither registers a handler nor reconciles a
    /// projection version, so a health probe can never trigger a rebuild.
    /// </summary>
    public async Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(tx, ct);

        long latestSeq;
        await using (var headCmd = conn.CreateCommand())
        {
            headCmd.Transaction = tx;
            headCmd.CommandText = $"SELECT COALESCE(MAX(seq), 0) FROM {Table}";
            latestSeq = (long)(await headCmd.ExecuteScalarAsync(ct))!;
        }

        // Lag = committed rows not yet delivered (ADR-022), counted — a seq difference has
        // no meaning once delivery follows causal order.
        var snapshots = new List<ChangeFeedLagSnapshot>(_handlers.Count);
        foreach (var handler in _handlers)
        {
            var name = NameOf(handler);
            var cursor = await SnapshotCursor.ReadOrInitialAsync(conn, tx, CheckpointPrefix + name, ct);
            var lag = await cursor.CountUndeliveredAsync(conn, tx, Table, ct);

            var declared = _projectionVersions[name];
            snapshots.Add(new ChangeFeedLagSnapshot(name, cursor.LastSeq, latestSeq, lag)
            {
                ProjectionVersion = declared,
                Paused = declared is int d && cursor.ProjectionVersion > d,
            });
            _lagByHandler[name] = lag; // feeds the observable gauge
        }

        return snapshots;
    }

    /// <summary>The persisted failure entries (retrying and poison) of this processor's handlers.</summary>
    public Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct) =>
        PostgresFeedDiagnostics.GetFailuresAsync(_dataSource, _handlers.Select(NameOf), CheckpointPrefix, ct);

    /// <summary>Removes a failure entry so the next cycle retries the record immediately.</summary>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor.</exception>
    public async Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct)
    {
        EnsureKnownHandler(handlerName);
        return await PostgresFeedDiagnostics.RetryFailureAsync(_dataSource, handlerName, CheckpointPrefix, seq, ct);
    }

    // The checkpoint and failure tables are shared by both feeds and keyed by name only: an
    // unchecked name would let a typo create an orphan checkpoint row, or reach the other
    // feed's entry ("event:orders" belongs to the event feed).
    private void EnsureKnownHandler(string handlerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        if (!_projectionVersions.ContainsKey(handlerName))
        {
            throw new ArgumentException(
                $"No handler named '{handlerName}' runs in this {FeedTag} feed processor.", nameof(handlerName));
        }
    }

    // ── Delivery ─────────────────────────────────────────────────────────────────

    private async Task<int> ProcessHandlerBatchAsync(THandler handler, CancellationToken ct)
    {
        var name = NameOf(handler);
        var key = CheckpointPrefix + name;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(tx, ct);

        // Leader coordination: skip the handler when another processor holds its checkpoint.
        var locked = await SnapshotCursor.LockAsync(conn, tx, key, ct);
        if (locked is null)
        {
            return 0; // locked by another instance (or not yet registered)
        }

        if (IsPaused(name, locked.ProjectionVersion))
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var cursor = await locked.WithSliceAsync(conn, tx, Table, ct);
        var batch = cursor.SliceSnapshot is null ? [] : await LoadBatchAsync(conn, tx, key, cursor, ct);
        if (batch.Count == 0 && locked.SliceSnapshot is not null)
        {
            // The slice in progress has run dry, so it is complete — go straight on with a
            // fresh one: a drained slice must not make the cycle look idle while newer
            // commits wait.
            cursor = await cursor.After(cursor.SliceSeq, 0, sliceCompleted: true).WithSliceAsync(conn, tx, Table, ct);
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
            var seq = SeqOf(item.Record);

            if (item.Attempts >= _options.MaxAttempts)
            {
                // Poison: skip, keep the failure entry as the permanent record.
                _logger.LogError(
                    "Handler {Handler} skips poison {Kind} seq {Seq} after {Attempts} attempts.",
                    name, FeedTag, seq, item.Attempts);
                KernelDiagnostics.FeedPoisoned.Add(1,
                    new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                    new KeyValuePair<string, object?>("papuma.handler", name));
                position = seq;
                highest = Math.Max(highest, seq);
                continue;
            }

            if (item.RetryPending)
            {
                stopped = true;
                break; // stop-the-line: strict ordering, retry after backoff (ADR-009)
            }

            // Handler span links to the originating write's trace (metadata traceparent).
            using var activity = FeedDiagnostics.StartHandlerActivity(FeedTag, name, seq, MetadataOf(item.Record));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await HandleAsync(handler, item.Record, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown; an OCE the handler raised on its own (HttpClient timeout) is a failure below
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, FeedDiagnostics.SanitizeError(ex));
                KernelDiagnostics.FeedFailures.Add(1,
                    new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                    new KeyValuePair<string, object?>("papuma.handler", name));
                var attempts = await RegisterFailureAsync(conn, tx, key, seq, ex, ct);
                _logger.LogWarning(ex,
                    "Handler {Handler} failed on {Kind} seq {Seq} (attempt {Attempts}/{MaxAttempts}).",
                    name, FeedTag, seq, attempts, _options.MaxAttempts);
                stopped = true;
                break; // stop-the-line; checkpoint stays before the failed seq
            }

            KernelDiagnostics.HandlerDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                new KeyValuePair<string, object?>("papuma.handler", name));
            KernelDiagnostics.FeedProcessed.Add(1,
                new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                new KeyValuePair<string, object?>("papuma.handler", name));

            if (item.Attempts > 0)
            {
                await ClearFailureAsync(conn, tx, key, seq, ct);
            }

            position = seq;
            highest = Math.Max(highest, seq);
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
    private sealed record BatchItem(TRecord Record, int Attempts, bool RetryPending);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string checkpointKey, SnapshotCursor cursor, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // The next rows of the cursor's slice: transactions committed since the done
        // snapshot, whole, in seq order (ADR-022).
        var slice = cursor.SlicePredicate(cmd, Alias);
        cmd.CommandText = $"""
            SELECT {RecordColumns}, COALESCE(f.attempts, 0),
                   COALESCE(f.next_retry_at > now(), false) AS retry_pending
            FROM {Table} {Alias}
            LEFT JOIN papuma.failure f
                   ON f.handler_name = @name AND f.seq = {Alias}.seq
            WHERE {slice}
            ORDER BY {Alias}.seq
            LIMIT @batchSize
            """;
        cmd.Parameters.AddWithValue("name", checkpointKey);
        cmd.Parameters.AddWithValue("batchSize", _options.BatchSize);

        var batch = new List<BatchItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var seq = reader.GetInt64(0);
            try
            {
                batch.Add(new BatchItem(
                    Map(reader),
                    reader.GetInt32(RecordColumnCount),
                    reader.GetBoolean(RecordColumnCount + 1)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    $"{FeedName} row seq {seq} could not be mapped to a record for handler {checkpointKey}: {ex.Message}", ex);
            }
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

        // Before the first delivery: new checkpoints, and projection versions reconciled
        // (record, rebuild once, or pause for a newer instance — ADR-024).
        foreach (var handler in _handlers)
        {
            var name = NameOf(handler);
            await ProjectionLifecycle.RegisterAsync(
                _dataSource, CheckpointPrefix + name, Table, handler, _projectionVersions[name], _logger, ct);
        }

        _registered = true;
    }

    // An instance declaring a lower version than the stored one runs older code: it must
    // not write the old shape into the target a newer instance rebuilt (ADR-024).
    private bool IsPaused(string handlerName, int? storedVersion)
    {
        if (_projectionVersions[handlerName] is not int declared || storedVersion is not int stored || stored <= declared)
        {
            return false;
        }

        if (_pauseLogged.TryAdd(handlerName, true))
        {
            _logger.LogWarning(
                "Projection {Handler} is at version {Stored}, this instance declares {Declared} — paused until this instance is replaced.",
                handlerName, stored, declared);
        }

        return true;
    }
}
