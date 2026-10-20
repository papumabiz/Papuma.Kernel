// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Diagnostics;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Processing;

/// <summary>
/// The delivery engine behind the SQLite change feed and event feed processors — the
/// single-writer counterpart of <c>PostgresFeedEngine</c>: strict <c>seq</c> order,
/// persisted checkpoints, retry with backoff, poison skipping, projection lifecycle, lag and
/// failure diagnostics. A feed extends it with its table, checkpoint-key prefix, the
/// record's columns and how a row becomes a record; the public processors are thin
/// facades, so a fix lands once.
/// </summary>
/// <remarks>
/// <para>
/// <b>No gapless-read handling (ADR-010 doesn't apply here):</b> that mechanism exists
/// solely for concurrent writers whose commit order can cross their seq-assignment order.
/// A single-writer embedded store has exactly one writer, serialized by SQLite's own file
/// lock — a plain <c>seq &gt; checkpoint</c> read is already correct. <b>No leader
/// coordination</b> (<c>FOR UPDATE SKIP LOCKED</c>) for the same reason, and <b>no
/// LISTEN/NOTIFY</b>: an optional in-process <see cref="SqliteChangeNotifier"/> wakes the loop.
/// </para>
/// <para>
/// <b>Handlers run with no transaction open.</b> A cycle reads checkpoint and batch in a
/// short transaction, runs the handlers, then records the outcome in a short write
/// transaction — only if the checkpoint is still the one read (a concurrent checkpoint reset
/// wins; the stale outcome is dropped). The database file's single write lock is therefore
/// free while handlers run: a projection writes to the same file through its own connection,
/// and a slow handler does not block the application. Run one processor per database file
/// (the hosted service does): nothing serializes two instances, and a second one would
/// deliver the same batch twice — still at-least-once, but pointless work.
/// </para>
/// </remarks>
/// <typeparam name="THandler">The feed's handler interface.</typeparam>
/// <typeparam name="TRecord">The record a handler receives.</typeparam>
internal abstract class SqliteFeedEngine<THandler, TRecord> : IDisposable
    where THandler : class
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<THandler> _handlers;
    private readonly ChangeFeedProcessorOptions _options;
    private readonly SqliteChangeNotifier? _notifier;
    private readonly ILogger _logger;

    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, long> _lagByHandler = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _projectionVersions;
    private readonly ConcurrentDictionary<string, bool> _pauseLogged = new(StringComparer.Ordinal);
    private bool _registered;

    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="handlers">The feed's handlers.</param>
    /// <param name="notifier">Optional in-process wakeup signal; <c>null</c> falls back to pure polling.</param>
    /// <param name="options">Engine options (optional).</param>
    /// <param name="logger">The processor's logger.</param>
    /// <param name="handlersParamName">Parameter name reported for an invalid handler list.</param>
    /// <exception cref="ArgumentException">A handler name is missing, invalid or used twice.</exception>
    protected SqliteFeedEngine(
        string connectionString,
        IEnumerable<THandler> handlers,
        SqliteChangeNotifier? notifier,
        ChangeFeedProcessorOptions? options,
        ILogger logger,
        string handlersParamName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(handlers);

        _connectionString = connectionString;
        _handlers = handlers.ToList();
        _options = options ?? new ChangeFeedProcessorOptions();
        _options.Validate();
        _notifier = notifier;
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
            description: "Feed head minus checkpoint, per handler.");
    }

    // ── What a feed supplies ─────────────────────────────────────────────────────

    /// <summary>The metric tag and the noun in log messages: <c>change</c> or <c>event</c>.</summary>
    protected abstract string FeedTag { get; }

    /// <summary>The feed's name in log messages: <c>Change</c> or <c>Event</c>.</summary>
    protected abstract string FeedName { get; }

    /// <summary>The feed table (<c>change</c> or <c>event</c>).</summary>
    protected abstract string Table { get; }

    /// <summary>
    /// The prefix of the handler's key in <c>checkpoint</c> and <c>failure</c> (shared
    /// tables, keyed by name only): empty for the change feed, <c>event:</c> for the event feed.
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

    protected abstract System.Text.Json.Nodes.JsonObject MetadataOf(TRecord record);

    /// <summary>Maps the current row (the first <see cref="RecordColumnCount"/> columns) to a record.</summary>
    protected abstract TRecord Map(SqliteDataReader reader);

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
    /// an in-process wakeup or the poll interval.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_handlers.Count == 0)
        {
            _logger.LogInformation("{Feed} feed processor idle — no handlers registered.", FeedName);
            return;
        }

        _logger.LogInformation("{Feed} feed processor started ({HandlerCount} handlers).", FeedName, _handlers.Count);

        // Own signal buffer, taken before the first cycle: a commit during any cycle wakes
        // this processor right after it — the other feed processor gets its own signal.
        using var subscription = _notifier?.Subscribe();

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
                    // Never retry a failing cycle at once: a missing file or a locked database
                    // must not turn the worker into a busy loop.
                    await Task.Delay(FeedLoop.BackoffDelay(_options.PollInterval, faultedCycles), ct);
                }
                else
                {
                    await TryRefreshLagCacheAsync(ct);
                    if (subscription is not null)
                    {
                        await subscription.WaitAsync(_options.PollInterval, ct);
                    }
                    else
                    {
                        await Task.Delay(_options.PollInterval, ct);
                    }
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

    // A fault in one handler's batch (an unmappable row, a busy database on the outcome
    // write) must not keep the handlers after it from running: each one is isolated, the
    // faults are reported.
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

        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await SqliteProjectionLifecycle.ResetCursorAsync(conn, tx, CheckpointPrefix + handlerName, version: null, ct);
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
        return await SqliteProjectionLifecycle.ResetProjectionAsync(
            _connectionString, CheckpointPrefix + handlerName, projection, declared, _logger, ct);
    }

    /// <summary>
    /// A pure read of the lag per handler (feed head vs. checkpoint): it neither registers a
    /// handler nor reconciles a projection version, so a dashboard or health probe can never
    /// trigger a rebuild. A handler that has not been registered yet shows the whole backlog.
    /// </summary>
    public async Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct)
    {
        // Deferred: a lag read must not take the file's write lock (it runs every idle cycle).
        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        await using var tx = conn.BeginTransaction(deferred: true);

        long latestSeq;
        await using (var headCmd = conn.CreateCommand())
        {
            headCmd.Transaction = tx;
            headCmd.CommandText = $"SELECT COALESCE(MAX(seq), 0) FROM {Table}";
            latestSeq = (long)(await headCmd.ExecuteScalarAsync(ct))!;
        }

        var snapshots = new List<ChangeFeedLagSnapshot>(_handlers.Count);
        foreach (var handler in _handlers)
        {
            var name = NameOf(handler);
            await using var checkpointCmd = conn.CreateCommand();
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = "SELECT last_seq FROM checkpoint WHERE handler_name = @name";
            checkpointCmd.Parameters.AddWithValue("name", CheckpointPrefix + name);
            var checkpoint = await checkpointCmd.ExecuteScalarAsync(ct) is long seq ? seq : 0L;

            var lag = latestSeq > checkpoint ? latestSeq - checkpoint : 0L;
            var declared = _projectionVersions[name];
            var stored = await SqliteProjectionLifecycle.ReadVersionAsync(conn, tx, CheckpointPrefix + name, ct);
            snapshots.Add(new ChangeFeedLagSnapshot(name, checkpoint, latestSeq, lag)
            {
                ProjectionVersion = declared,
                Paused = declared is int d && stored > d,
            });
            _lagByHandler[name] = lag; // feeds the observable gauge
        }

        await tx.CommitAsync(ct);
        return snapshots;
    }

    /// <summary>The persisted failure entries (retrying and poison) of this processor's handlers.</summary>
    public async Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct)
    {
        var keys = _handlers.Select(h => CheckpointPrefix + NameOf(h)).ToArray();

        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT handler_name, seq, attempts, last_error, next_retry_at, updated_at
            FROM failure
            WHERE handler_name IN (SELECT value FROM json_each(@names))
            ORDER BY handler_name, seq
            """;
        cmd.Parameters.AddWithValue("names", System.Text.Json.JsonSerializer.Serialize(keys));

        var failures = new List<FeedFailure>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            failures.Add(new FeedFailure(
                HandlerName: reader.GetString(0)[CheckpointPrefix.Length..],
                Seq: reader.GetInt64(1),
                Attempts: reader.GetInt32(2),
                LastError: reader.GetString(3),
                NextRetryAt: SqliteTimestamp.Parse(reader.GetString(4)),
                UpdatedAt: SqliteTimestamp.Parse(reader.GetString(5))));
        }

        return failures;
    }

    /// <summary>Removes a failure entry so the next cycle retries the record immediately.</summary>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor.</exception>
    public async Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct)
    {
        EnsureKnownHandler(handlerName);

        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM failure WHERE handler_name = @name AND seq = @seq";
        cmd.Parameters.AddWithValue("name", CheckpointPrefix + handlerName);
        cmd.Parameters.AddWithValue("seq", seq);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
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

        // Three short phases instead of one transaction around the handlers: a handler
        // may write to the same database file through its own connection (a local
        // projection), and a slow handler (mail, HTTP) must not hold the file's single
        // write lock against the application. Guarantees are unchanged — at-least-once,
        // strict order, stop-the-line — because the checkpoint still only moves after
        // the handler returned.

        // 1. Read: one consistent snapshot of checkpoint and batch.
        long checkpoint;
        long generation; // the checkpoint's reset_count: told apart from "reset to the same position"
        IReadOnlyList<BatchItem> batch;
        await using (var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct))
        await using (var tx = conn.BeginTransaction(deferred: true))
        {
            (checkpoint, generation) = await SqliteProjectionLifecycle.ReadCursorAsync(conn, tx, key, ct);
            batch = await IsPausedAsync(name, conn, tx, key, ct)
                ? []
                : await LoadBatchAsync(conn, tx, key, checkpoint, ct);
            await tx.CommitAsync(ct);
        }

        if (batch.Count == 0)
        {
            return 0;
        }

        // 2. Handle: no transaction is open.
        var processed = 0;
        var newCheckpoint = checkpoint;
        var recovered = new List<long>();
        (long Seq, Exception Error)? failed = null;
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
                newCheckpoint = seq;
                continue;
            }

            if (item.NextRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
            {
                break; // stop-the-line: strict ordering, retry after backoff
            }

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
                failed = (seq, ex);
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
                recovered.Add(seq);
            }

            newCheckpoint = seq;
            processed++;
        }

        // 3. Record the outcome — only against the checkpoint it was computed from. If it
        // moved meanwhile (a reset for a rebuild), the outcome belongs to a stale position:
        // drop it, the next cycle reads from the new one.
        await using (var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct))
        await using (var tx = conn.BeginTransaction(deferred: false))
        {
            // Position AND generation: a reset from 0 to 0 (backfill, rebuild) leaves last_seq
            // unchanged, but not the generation (review L-3).
            if (await SqliteProjectionLifecycle.ReadCursorAsync(conn, tx, key, ct) != (checkpoint, generation))
            {
                await tx.RollbackAsync(ct);
                _logger.LogInformation(
                    "Checkpoint of {Handler} moved while its batch was handled; outcome dropped, next cycle re-reads.",
                    name);
                return 0;
            }

            foreach (var seq in recovered)
            {
                await ClearFailureAsync(conn, tx, key, seq, ct);
            }

            if (failed is { } failure)
            {
                var attempts = await RegisterFailureAsync(conn, tx, key, failure.Seq, failure.Error, ct);
                _logger.LogWarning(failure.Error,
                    "Handler {Handler} failed on {Kind} seq {Seq} (attempt {Attempts}/{MaxAttempts}).",
                    name, FeedTag, failure.Seq, attempts, _options.MaxAttempts);
            }

            if (newCheckpoint != checkpoint)
            {
                await SaveCheckpointAsync(conn, tx, key, newCheckpoint, ct);
            }

            await tx.CommitAsync(ct);
        }

        return processed;
    }

    private sealed record BatchItem(TRecord Record, int Attempts, DateTimeOffset? NextRetryAt);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        SqliteConnection conn, SqliteTransaction tx, string checkpointKey, long checkpoint, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT {RecordColumns}, COALESCE(f.attempts, 0), f.next_retry_at
            FROM {Table} {Alias}
            LEFT JOIN failure f
                   ON f.handler_name = @name AND f.seq = {Alias}.seq
            WHERE {Alias}.seq > @checkpoint
            ORDER BY {Alias}.seq
            LIMIT @batchSize
            """;
        cmd.Parameters.AddWithValue("name", checkpointKey);
        cmd.Parameters.AddWithValue("checkpoint", checkpoint);
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
                    reader.IsDBNull(RecordColumnCount + 1)
                        ? null
                        : SqliteTimestamp.Parse(reader.GetString(RecordColumnCount + 1))));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    $"{FeedName} row seq {seq} could not be mapped to a record for handler {checkpointKey}: {ex.Message}", ex);
            }
        }

        return batch;
    }

    private static async Task SaveCheckpointAsync(
        SqliteConnection conn, SqliteTransaction tx, string checkpointKey, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE checkpoint SET last_seq = @seq, updated_at = @now WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", checkpointKey);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Records a failure and computes the exponential backoff in C# — SQLite has no
    /// <c>now() + INTERVAL</c> arithmetic the way Postgres does, so the "attempts before
    /// this one" read happens first and the delay is computed from it directly (same
    /// formula as the Postgres kernel's <c>LEAST(baseDelay * POWER(2, attempts), maxDelay)</c>).
    /// </summary>
    private async Task<int> RegisterFailureAsync(
        SqliteConnection conn, SqliteTransaction tx, string checkpointKey, long seq, Exception ex, CancellationToken ct)
    {
        int attemptsBefore;
        await using (var readCmd = conn.CreateCommand())
        {
            readCmd.Transaction = tx;
            readCmd.CommandText = "SELECT attempts FROM failure WHERE handler_name = @name AND seq = @seq";
            readCmd.Parameters.AddWithValue("name", checkpointKey);
            readCmd.Parameters.AddWithValue("seq", seq);
            attemptsBefore = await readCmd.ExecuteScalarAsync(ct) is long a ? (int)a : 0;
        }

        var attempts = attemptsBefore + 1;
        var delaySeconds = Math.Min(
            _options.BaseRetryDelay.TotalSeconds * Math.Pow(2, attemptsBefore),
            _options.MaxRetryDelay.TotalSeconds);
        var nextRetryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToString("O");
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO failure (handler_name, seq, attempts, last_error, next_retry_at, updated_at)
            VALUES (@name, @seq, 1, @error, @nextRetryAt, @now)
            ON CONFLICT (handler_name, seq) DO UPDATE SET
                attempts = attempts + 1,
                last_error = excluded.last_error,
                next_retry_at = @nextRetryAt,
                updated_at = @now
            """;
        cmd.Parameters.AddWithValue("name", checkpointKey);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("error", FeedDiagnostics.SanitizeError(ex));
        cmd.Parameters.AddWithValue("nextRetryAt", nextRetryAt);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        return attempts;
    }

    private static async Task ClearFailureAsync(
        SqliteConnection conn, SqliteTransaction tx, string checkpointKey, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM failure WHERE handler_name = @name AND seq = @seq";
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
        // (record, rebuild once, or pause for a newer version — ADR-024).
        foreach (var handler in _handlers)
        {
            var name = NameOf(handler);
            await SqliteProjectionLifecycle.RegisterAsync(
                _connectionString, CheckpointPrefix + name, Table, handler, _projectionVersions[name], _logger, ct);
        }

        _registered = true;
    }

    // A stored version above the declared one means newer code rebuilt the projection;
    // this (older) code must not write the old shape into it (ADR-024).
    private async Task<bool> IsPausedAsync(
        string handlerName, SqliteConnection conn, SqliteTransaction tx, string key, CancellationToken ct)
    {
        if (_projectionVersions[handlerName] is not int declared
            || await SqliteProjectionLifecycle.ReadVersionAsync(conn, tx, key, ct) is not int stored
            || stored <= declared)
        {
            return false;
        }

        if (_pauseLogged.TryAdd(handlerName, true))
        {
            _logger.LogWarning(
                "Projection {Handler} is at version {Stored}, this instance declares {Declared} — paused.",
                handlerName, stored, declared);
        }

        return true;
    }
}
