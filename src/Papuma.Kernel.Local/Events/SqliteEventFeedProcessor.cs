// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Papuma.Kernel.Diagnostics;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// The event log engine for a single-writer embedded store — SQLite counterpart of the
/// Postgres kernel's <c>EventFeedProcessor</c>, with the same simplifications as
/// <see cref="SqliteChangeFeedProcessor"/> (no gapless-read handling, no leader
/// coordination, in-process wakeup instead of LISTEN/NOTIFY, handlers run with no
/// transaction open, one processor per database file — see that type's remarks).
/// </summary>
/// <remarks>
/// Change feed and event log are separate feeds with separate checkpoint spaces
/// (handler names are prefixed with <c>event:</c> in the <c>checkpoint</c> table) — no
/// global ordering across the two feeds; correlate via <c>correlationId</c>.
/// </remarks>
public sealed class SqliteEventFeedProcessor : IDisposable
{
    private const string CheckpointPrefix = "event:";
    private const string FeedTag = "event";

    private readonly string _connectionString;
    private readonly IReadOnlyList<IEventHandler> _handlers;
    private readonly ChangeFeedProcessorOptions _options;
    private readonly SqliteChangeNotifier? _notifier;
    private readonly ILogger _logger;

    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, long> _lagByHandler = new(StringComparer.Ordinal);
    private bool _registered;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteEventFeedProcessor"/> class.
    /// </summary>
    public SqliteEventFeedProcessor(
        string connectionString,
        IEnumerable<IEventHandler> handlers,
        SqliteChangeNotifier? notifier = null,
        ChangeFeedProcessorOptions? options = null,
        ILogger<SqliteEventFeedProcessor>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(handlers);

        _connectionString = connectionString;
        _handlers = handlers.ToList();
        _options = options ?? new ChangeFeedProcessorOptions();
        _options.Validate();
        _notifier = notifier;
        _logger = logger ?? NullLogger<SqliteEventFeedProcessor>.Instance;

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
            description: "Feed head minus checkpoint, per handler.");
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
    /// Runs the processing loop until cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_handlers.Count == 0)
        {
            _logger.LogInformation("Event feed processor idle — no handlers registered.");
            return;
        }

        _logger.LogInformation("Event feed processor started ({HandlerCount} handlers).", _handlers.Count);

        // Own signal buffer, taken before the first cycle: a commit during any cycle wakes
        // this processor right after it — the other feed processor gets its own signal.
        using var subscription = _notifier?.Subscribe();

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
                    await GetLagAsync(ct);
                    if (subscription is not null)
                    {
                        await subscription.WaitAsync(_options.PollInterval, ct);
                    }
                    else
                    {
                        await Task.Delay(_options.PollInterval, ct);
                    }
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
    /// Returns the persisted failure entries (retrying and poison) of this processor's handlers.
    /// </summary>
    public async Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct = default)
    {
        var keys = _handlers.Select(h => CheckpointPrefix + h.Name).ToArray();

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
                NextRetryAt: DateTimeOffset.Parse(reader.GetString(4)),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(5))));
        }

        return failures;
    }

    /// <summary>
    /// Removes a failure entry so the next cycle retries the event immediately.
    /// </summary>
    public async Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);

        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM failure WHERE handler_name = @name AND seq = @seq";
        cmd.Parameters.AddWithValue("name", CheckpointPrefix + handlerName);
        cmd.Parameters.AddWithValue("seq", seq);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Resets a handler's checkpoint to 0 and clears its failure entries.
    /// </summary>
    public async Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        var key = CheckpointPrefix + handlerName;

        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var checkpointCmd = conn.CreateCommand())
        {
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = """
                INSERT INTO checkpoint (handler_name, last_seq, updated_at)
                VALUES (@name, 0, @now)
                ON CONFLICT (handler_name) DO UPDATE SET last_seq = 0, updated_at = @now
                """;
            checkpointCmd.Parameters.AddWithValue("name", key);
            checkpointCmd.Parameters.AddWithValue("now", now);
            await checkpointCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var failureCmd = conn.CreateCommand())
        {
            failureCmd.Transaction = tx;
            failureCmd.CommandText = "DELETE FROM failure WHERE handler_name = @name";
            failureCmd.Parameters.AddWithValue("name", key);
            await failureCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Returns a lag snapshot per handler (event log head vs. checkpoint).
    /// </summary>
    public async Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct = default)
    {
        await EnsureRegisteredAsync(ct);

        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        long latestSeq;
        await using (var headCmd = conn.CreateCommand())
        {
            headCmd.Transaction = tx;
            headCmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM event";
            latestSeq = (long)(await headCmd.ExecuteScalarAsync(ct))!;
        }

        var snapshots = new List<ChangeFeedLagSnapshot>(_handlers.Count);
        foreach (var handler in _handlers)
        {
            await using var checkpointCmd = conn.CreateCommand();
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = "SELECT last_seq FROM checkpoint WHERE handler_name = @name";
            checkpointCmd.Parameters.AddWithValue("name", CheckpointPrefix + handler.Name);
            var checkpoint = await checkpointCmd.ExecuteScalarAsync(ct) is long seq ? seq : 0L;

            var lag = latestSeq > checkpoint ? latestSeq - checkpoint : 0L;
            snapshots.Add(new ChangeFeedLagSnapshot(handler.Name, checkpoint, latestSeq, lag));
            _lagByHandler[handler.Name] = lag;
        }

        await tx.CommitAsync(ct);
        return snapshots;
    }

    private async Task<int> ProcessHandlerBatchAsync(IEventHandler handler, CancellationToken ct)
    {
        var key = CheckpointPrefix + handler.Name;

        // Three short phases instead of one transaction around the handlers: a handler
        // may write to the same database file through its own connection (a local
        // projection), and a slow handler (mail, HTTP) must not hold the file's single
        // write lock against the application. Guarantees are unchanged — at-least-once,
        // strict order, stop-the-line — because the checkpoint still only moves after
        // the handler returned.

        // 1. Read: one consistent snapshot of checkpoint and batch.
        long checkpoint;
        IReadOnlyList<BatchItem> batch;
        await using (var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct))
        await using (var tx = conn.BeginTransaction(deferred: true))
        {
            checkpoint = await ReadCheckpointAsync(conn, tx, key, ct);
            batch = await LoadBatchAsync(conn, tx, key, checkpoint, ct);
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

            if (item.Attempts >= _options.MaxAttempts)
            {
                // Poison: skip, keep the failure entry as the permanent record.
                _logger.LogError(
                    "Event handler {Handler} skips poison event seq {Seq} after {Attempts} attempts.",
                    handler.Name, item.Record.Seq, item.Attempts);
                KernelDiagnostics.FeedPoisoned.Add(1,
                    new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                    new KeyValuePair<string, object?>("papuma.handler", handler.Name));
                newCheckpoint = item.Record.Seq;
                continue;
            }

            if (item.NextRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
            {
                break; // stop-the-line: strict ordering, retry after backoff
            }

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
                failed = (item.Record.Seq, ex);
                break; // stop-the-line; checkpoint stays before the failed seq
            }

            KernelDiagnostics.HandlerDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                new KeyValuePair<string, object?>("papuma.handler", handler.Name));
            KernelDiagnostics.FeedProcessed.Add(1,
                new KeyValuePair<string, object?>("papuma.feed", FeedTag),
                new KeyValuePair<string, object?>("papuma.handler", handler.Name));

            if (item.Attempts > 0)
            {
                recovered.Add(item.Record.Seq);
            }

            newCheckpoint = item.Record.Seq;
            processed++;
        }

        // 3. Record the outcome — only against the checkpoint it was computed from. If it
        // moved meanwhile (a reset for a rebuild), the outcome belongs to a stale position:
        // drop it, the next cycle reads from the new one.
        await using (var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct))
        await using (var tx = conn.BeginTransaction(deferred: false))
        {
            if (await ReadCheckpointAsync(conn, tx, key, ct) != checkpoint)
            {
                await tx.RollbackAsync(ct);
                _logger.LogInformation(
                    "Checkpoint of {Handler} moved while its batch was handled; outcome dropped, next cycle re-reads.",
                    handler.Name);
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
                    "Event handler {Handler} failed on event seq {Seq} (attempt {Attempts}/{MaxAttempts}).",
                    handler.Name, failure.Seq, attempts, _options.MaxAttempts);
            }

            if (newCheckpoint != checkpoint)
            {
                await SaveCheckpointAsync(conn, tx, key, newCheckpoint, ct);
            }

            await tx.CommitAsync(ct);
        }

        return processed;
    }

    private static async Task<long> ReadCheckpointAsync(
        SqliteConnection conn, SqliteTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT last_seq FROM checkpoint WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", key);
        return await cmd.ExecuteScalarAsync(ct) is long seq ? seq : 0L;
    }

    private static async Task SaveCheckpointAsync(
        SqliteConnection conn, SqliteTransaction tx, string handlerName, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE checkpoint SET last_seq = @seq, updated_at = @now WHERE handler_name = @name";
        cmd.Parameters.AddWithValue("name", handlerName);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record BatchItem(EventRecord Record, int Attempts, DateTimeOffset? NextRetryAt);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        SqliteConnection conn, SqliteTransaction tx, string checkpointKey, long checkpoint, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.seq, e.scope, e.tenant_id, e.event_type, e.payload, e.actor_id,
                   e.metadata, e.occurred_at, COALESCE(f.attempts, 0), f.next_retry_at
            FROM event e
            LEFT JOIN failure f
                   ON f.handler_name = @name AND f.seq = e.seq
            WHERE e.seq > @checkpoint
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
                ActorId: reader.GetString(5),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(6))!,
                OccurredAt: DateTimeOffset.Parse(reader.GetString(7)));

            batch.Add(new BatchItem(
                record,
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9))));
        }

        return batch;
    }

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

        await using var conn = await SqliteConnectionFactory.OpenAsync(_connectionString, ct);
        foreach (var handler in _handlers)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO checkpoint (handler_name, last_seq, updated_at)
                VALUES (@name, 0, @now)
                ON CONFLICT (handler_name) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("name", CheckpointPrefix + handler.Name);
            cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _registered = true;
    }
}
