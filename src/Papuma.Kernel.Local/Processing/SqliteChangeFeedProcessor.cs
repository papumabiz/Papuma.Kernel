// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Diagnostics;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Processing;

/// <summary>
/// The change feed engine for a single-writer embedded store — SQLite counterpart of
/// the Postgres kernel's <see cref="ChangeFeedProcessor"/> (referenced only in doc
/// comments here; the type itself lives in <c>Papuma.Kernel</c> and is not reused,
/// see docs/analyses/local-kernel-sqlite-sibling.md). Delivers committed changes to
/// registered handlers in strict <c>seq</c> order with persisted checkpoints, retry with
/// exponential backoff, poison skipping.
/// </summary>
/// <remarks>
/// <para>
/// <b>No gapless-read handling (ADR-010 doesn't apply here):</b> that mechanism exists
/// solely for concurrent writers whose commit order can cross their seq-assignment order.
/// A single-writer embedded store has exactly one writer, serialized by SQLite's own file
/// lock — a plain <c>seq &gt; checkpoint</c> read is already correct.
/// </para>
/// <para>
/// <b>No leader coordination</b> (Postgres's <c>FOR UPDATE SKIP LOCKED</c>): that exists
/// to let multiple concurrent processor instances skip a handler another instance is
/// working on. A single-process store has one processor instance; there's nothing to
/// coordinate.
/// </para>
/// <para>
/// <b>No LISTEN/NOTIFY:</b> replaced by an optional in-process <see cref="SqliteChangeNotifier"/>.
/// </para>
/// </remarks>
public sealed class SqliteChangeFeedProcessor : IDisposable
{
    private const string FeedTag = "change";

    private readonly string _connectionString;
    private readonly IReadOnlyList<IChangeHandler> _handlers;
    private readonly ChangeFeedProcessorOptions _options;
    private readonly SqliteChangeNotifier? _notifier;
    private readonly ILogger _logger;

    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, long> _lagByHandler = new(StringComparer.Ordinal);
    private bool _registered;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteChangeFeedProcessor"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="handlers">The change handlers (unique names).</param>
    /// <param name="notifier">Optional in-process wakeup signal (Stage 4); <c>null</c> falls back to pure polling.</param>
    /// <param name="options">Engine options (optional).</param>
    /// <param name="logger">Logger (optional).</param>
    public SqliteChangeFeedProcessor(
        string connectionString,
        IEnumerable<IChangeHandler> handlers,
        SqliteChangeNotifier? notifier = null,
        ChangeFeedProcessorOptions? options = null,
        ILogger<SqliteChangeFeedProcessor>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(handlers);

        _connectionString = connectionString;
        _handlers = handlers.ToList();
        _options = options ?? new ChangeFeedProcessorOptions();
        _options.Validate();
        _notifier = notifier;
        _logger = logger ?? NullLogger<SqliteChangeFeedProcessor>.Instance;

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
    /// Runs the processing loop until cancellation: process all handlers, then wait for
    /// an in-process wakeup or the poll interval.
    /// </summary>
    /// <param name="ct">The cancellation token stopping the loop.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_handlers.Count == 0)
        {
            _logger.LogInformation("Change feed processor idle — no handlers registered.");
            return;
        }

        _logger.LogInformation("Change feed processor started ({HandlerCount} handlers).", _handlers.Count);

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
                _logger.LogError(ex, "Change feed processing cycle failed.");
                processed = 0;
            }

            if (processed == 0)
            {
                try
                {
                    await RefreshLagCacheAsync(ct);
                    if (_notifier is not null)
                    {
                        await _notifier.WaitAsync(_options.PollInterval, ct);
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

        _logger.LogInformation("Change feed processor stopped.");
    }

    /// <summary>
    /// Runs one processing cycle over all handlers and returns the number of delivered
    /// changes. Exposed for hosting integration and tests.
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
    /// Resets a handler's checkpoint to 0 and clears its failure entries — the next
    /// cycle replays the full feed.
    /// </summary>
    public async Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
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
            checkpointCmd.Parameters.AddWithValue("name", handlerName);
            checkpointCmd.Parameters.AddWithValue("now", now);
            await checkpointCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var failureCmd = conn.CreateCommand())
        {
            failureCmd.Transaction = tx;
            failureCmd.CommandText = "DELETE FROM failure WHERE handler_name = @name";
            failureCmd.Parameters.AddWithValue("name", handlerName);
            await failureCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Checkpoint for handler {Handler} reset — full replay on next cycle.", handlerName);
    }

    /// <summary>
    /// Returns a lag snapshot per handler (feed head vs. checkpoint).
    /// </summary>
    public async Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct = default)
    {
        await EnsureRegisteredAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        long latestSeq;
        await using (var headCmd = conn.CreateCommand())
        {
            headCmd.Transaction = tx;
            headCmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM change";
            latestSeq = (long)(await headCmd.ExecuteScalarAsync(ct))!;
        }

        var snapshots = new List<ChangeFeedLagSnapshot>(_handlers.Count);
        foreach (var handler in _handlers)
        {
            await using var checkpointCmd = conn.CreateCommand();
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = "SELECT last_seq FROM checkpoint WHERE handler_name = @name";
            checkpointCmd.Parameters.AddWithValue("name", handler.Name);
            var checkpoint = await checkpointCmd.ExecuteScalarAsync(ct) is long seq ? seq : 0L;

            var lag = latestSeq > checkpoint ? latestSeq - checkpoint : 0L;
            snapshots.Add(new ChangeFeedLagSnapshot(handler.Name, checkpoint, latestSeq, lag));
            _lagByHandler[handler.Name] = lag; // feeds the observable gauge
        }

        await tx.CommitAsync(ct);
        return snapshots;
    }

    private Task RefreshLagCacheAsync(CancellationToken ct) => GetLagAsync(ct);

    /// <summary>
    /// Returns the persisted failure entries (retrying and poison) of this processor's
    /// handlers.
    /// </summary>
    public async Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT handler_name, seq, attempts, last_error, next_retry_at, updated_at
            FROM failure
            WHERE handler_name IN (SELECT value FROM json_each(@names))
            ORDER BY handler_name, seq
            """;
        cmd.Parameters.AddWithValue("names", System.Text.Json.JsonSerializer.Serialize(_handlers.Select(h => h.Name)));

        var failures = new List<FeedFailure>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            failures.Add(new FeedFailure(
                HandlerName: reader.GetString(0),
                Seq: reader.GetInt64(1),
                Attempts: reader.GetInt32(2),
                LastError: reader.GetString(3),
                NextRetryAt: DateTimeOffset.Parse(reader.GetString(4)),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(5))));
        }

        return failures;
    }

    /// <summary>
    /// Removes a failure entry so the next cycle retries the record immediately.
    /// </summary>
    public async Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM failure WHERE handler_name = @name AND seq = @seq";
        cmd.Parameters.AddWithValue("name", handlerName);
        cmd.Parameters.AddWithValue("seq", seq);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<int> ProcessHandlerBatchAsync(IChangeHandler handler, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        long checkpoint;
        await using (var checkpointCmd = conn.CreateCommand())
        {
            checkpointCmd.Transaction = tx;
            checkpointCmd.CommandText = "SELECT last_seq FROM checkpoint WHERE handler_name = @name";
            checkpointCmd.Parameters.AddWithValue("name", handler.Name);
            checkpoint = await checkpointCmd.ExecuteScalarAsync(ct) is long seq ? seq : 0L;
        }

        var batch = await LoadBatchAsync(conn, tx, handler.Name, checkpoint, ct);
        if (batch.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var processed = 0;
        var newCheckpoint = checkpoint;
        foreach (var item in batch)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Attempts >= _options.MaxAttempts)
            {
                // Poison: skip, keep the failure entry as the permanent record.
                _logger.LogError(
                    "Handler {Handler} skips poison change seq {Seq} after {Attempts} attempts.",
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
                var attempts = await RegisterFailureAsync(conn, tx, handler.Name, item.Record.Seq, ex, ct);
                _logger.LogWarning(ex,
                    "Handler {Handler} failed on change seq {Seq} (attempt {Attempts}/{MaxAttempts}).",
                    handler.Name, item.Record.Seq, attempts, _options.MaxAttempts);
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
                await ClearFailureAsync(conn, tx, handler.Name, item.Record.Seq, ct);
            }

            newCheckpoint = item.Record.Seq;
            processed++;
        }

        if (newCheckpoint != checkpoint)
        {
            await SaveCheckpointAsync(conn, tx, handler.Name, newCheckpoint, ct);
        }

        await tx.CommitAsync(ct);
        return processed;
    }

    private sealed record BatchItem(ChangeRecord Record, int Attempts, DateTimeOffset? NextRetryAt);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        SqliteConnection conn, SqliteTransaction tx, string handlerName, long checkpoint, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT c.seq, c.scope, c.tenant_id, c.document_type, c.document_id, c.version,
                   c.schema_version, c.operation, c.diff, c.actor_id, c.metadata,
                   c.occurred_at, COALESCE(f.attempts, 0), f.next_retry_at
            FROM change c
            LEFT JOIN failure f
                   ON f.handler_name = @name AND f.seq = c.seq
            WHERE c.seq > @checkpoint
            ORDER BY c.seq
            LIMIT @batchSize
            """;
        cmd.Parameters.AddWithValue("name", handlerName);
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

            var record = new ChangeRecord(
                Seq: reader.GetInt64(0),
                Scope: scope,
                DocumentType: reader.GetString(3),
                DocumentId: reader.GetString(4),
                Version: reader.GetInt64(5),
                SchemaVersion: reader.GetInt32(6),
                Operation: (ChangeOperation)reader.GetInt32(7),
                Diff: DocumentDiff.FromJson((JsonObject)JsonNode.Parse(reader.GetString(8))!),
                ActorId: reader.GetString(9),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(10))!,
                OccurredAt: DateTimeOffset.Parse(reader.GetString(11)));

            batch.Add(new BatchItem(
                record,
                reader.GetInt32(12),
                reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13))));
        }

        return batch;
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

    /// <summary>
    /// Records a failure and computes the exponential backoff in C# — SQLite has no
    /// <c>now() + INTERVAL</c> arithmetic the way Postgres does, so the "attempts before
    /// this one" read happens first and the delay is computed from it directly (same
    /// formula as the Postgres kernel's <c>LEAST(baseDelay * POWER(2, attempts), maxDelay)</c>).
    /// </summary>
    private async Task<int> RegisterFailureAsync(
        SqliteConnection conn, SqliteTransaction tx, string handlerName, long seq, Exception ex, CancellationToken ct)
    {
        int attemptsBefore;
        await using (var readCmd = conn.CreateCommand())
        {
            readCmd.Transaction = tx;
            readCmd.CommandText = "SELECT attempts FROM failure WHERE handler_name = @name AND seq = @seq";
            readCmd.Parameters.AddWithValue("name", handlerName);
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
        cmd.Parameters.AddWithValue("name", handlerName);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("error", FeedDiagnostics.SanitizeError(ex));
        cmd.Parameters.AddWithValue("nextRetryAt", nextRetryAt);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        return attempts;
    }

    private static async Task ClearFailureAsync(
        SqliteConnection conn, SqliteTransaction tx, string handlerName, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM failure WHERE handler_name = @name AND seq = @seq";
        cmd.Parameters.AddWithValue("name", handlerName);
        cmd.Parameters.AddWithValue("seq", seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        if (_registered)
        {
            return;
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        foreach (var handler in _handlers)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO checkpoint (handler_name, last_seq, updated_at)
                VALUES (@name, 0, @now)
                ON CONFLICT (handler_name) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("name", handler.Name);
            cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _registered = true;
    }
}
