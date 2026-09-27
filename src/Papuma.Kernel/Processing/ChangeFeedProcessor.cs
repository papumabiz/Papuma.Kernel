// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Diagnostics;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Processing;

/// <summary>
/// The change feed engine (ADR-009/010): delivers committed changes to registered
/// handlers in strict <c>seq</c> order with persisted checkpoints, retry with
/// exponential backoff, poison skipping, and rebuild support.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gapless reads (ADR-010):</b> only changes whose transaction lies before
/// <c>pg_snapshot_xmin(pg_current_snapshot())</c> count as stable-visible — a change
/// committed late by a long-running transaction can never be skipped.
/// </para>
/// <para>
/// <b>Leader coordination:</b> the per-handler checkpoint row is taken with
/// <c>FOR UPDATE SKIP LOCKED</c>; concurrent processor instances simply skip handlers
/// another instance is currently working on — no distributed consensus (ADR-010).
/// </para>
/// <para>
/// <b>Delivery:</b> at-least-once, stop-on-failure per handler (strict ordering); after
/// <see cref="ChangeFeedProcessorOptions.MaxAttempts"/> a change is skipped as poison
/// and stays recorded in <c>papuma.failure</c>.
/// </para>
/// </remarks>
public sealed class ChangeFeedProcessor : IDisposable
{
    /// <summary>The NOTIFY channel used as wakeup signal (ADR-010).</summary>
    public const string NotifyChannel = "papuma_changes";

    private const string FeedTag = "change";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<IChangeHandler> _handlers;
    private readonly ChangeFeedProcessorOptions _options;
    private readonly ILogger _logger;

    // Per-instance meter (same name as the shared one — listeners match by name) so the
    // observable lag gauge can be disposed with the processor (phase 11). Gauge values
    // come from a cache refreshed by GetLagAsync and the idle moments of RunAsync.
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, long> _lagByHandler = new(StringComparer.Ordinal);
    private bool _registered;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeFeedProcessor"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="handlers">The change handlers (unique names).</param>
    /// <param name="options">Engine options (optional).</param>
    /// <param name="logger">Logger (optional).</param>
    public ChangeFeedProcessor(
        NpgsqlDataSource dataSource,
        IEnumerable<IChangeHandler> handlers,
        ChangeFeedProcessorOptions? options = null,
        ILogger<ChangeFeedProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(handlers);

        _dataSource = dataSource;
        _handlers = handlers.ToList();
        _options = options ?? new ChangeFeedProcessorOptions();
        _options.Validate();
        _logger = logger ?? NullLogger<ChangeFeedProcessor>.Instance;

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
    /// Runs the processing loop until cancellation: process all handlers, then wait for
    /// a NOTIFY wakeup or the poll interval — polling stays the source of truth (ADR-010).
    /// </summary>
    /// <param name="ct">The cancellation token stopping the loop.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_handlers.Count == 0)
        {
            _logger.LogInformation("Change feed processor idle — no handlers registered.");
            return;
        }

        await using var listenConn = await _dataSource.OpenConnectionAsync(ct);
        await using (var listenCmd = listenConn.CreateCommand())
        {
            listenCmd.CommandText = $"LISTEN {NotifyChannel}";
            await listenCmd.ExecuteNonQueryAsync(ct);
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
                    await RefreshLagCacheAsync(ct); // gauge freshness ≈ poll interval
                    // Returns early on NOTIFY; otherwise the poll interval elapses.
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
    /// cycle replays the full feed (rebuild, ADR-009).
    /// </summary>
    /// <param name="handlerName">The handler name.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);

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
            checkpointCmd.Parameters.AddWithValue("name", handlerName);
            await checkpointCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var failureCmd = conn.CreateCommand())
        {
            failureCmd.Transaction = tx;
            failureCmd.CommandText = "DELETE FROM papuma.failure WHERE handler_name = @name";
            failureCmd.Parameters.AddWithValue("name", handlerName);
            await failureCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Checkpoint for handler {Handler} reset — full replay on next cycle.", handlerName);
    }

    /// <summary>
    /// Returns a lag snapshot per handler (feed head vs. checkpoint).
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
            headCmd.CommandText = """
                SELECT COALESCE(MAX(seq), 0)
                FROM papuma.change
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
            checkpointCmd.Parameters.AddWithValue("name", handler.Name);
            var checkpoint = await checkpointCmd.ExecuteScalarAsync(ct) is long seq ? seq : 0L;

            var lag = latestSeq > checkpoint ? latestSeq - checkpoint : 0L;
            snapshots.Add(new ChangeFeedLagSnapshot(handler.Name, checkpoint, latestSeq, lag));
            _lagByHandler[handler.Name] = lag; // feeds the observable gauge
        }

        return snapshots;
    }

    private Task RefreshLagCacheAsync(CancellationToken ct) => GetLagAsync(ct);

    /// <summary>
    /// Returns the persisted failure entries (retrying and poison) of this processor's
    /// handlers — the failure table as an API (phase 11 diagnostics).
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct = default) =>
        PostgresFeedDiagnostics.GetFailuresAsync(_dataSource, _handlers.Select(h => h.Name), prefix: string.Empty, ct);

    /// <summary>
    /// Removes a failure entry so the next cycle retries the record immediately —
    /// the manual override after fixing a poison cause.
    /// </summary>
    /// <param name="handlerName">The handler name.</param>
    /// <param name="seq">The feed sequence number.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>true</c> when a failure entry existed and was removed.</returns>
    public Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct = default) =>
        PostgresFeedDiagnostics.RetryFailureAsync(_dataSource, handlerName, prefix: string.Empty, seq, ct);

    private async Task<int> ProcessHandlerBatchAsync(IChangeHandler handler, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetAllScopesAsync(tx, ct);

        // Leader coordination: skip the handler when another processor holds its checkpoint.
        long? checkpoint;
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = """
                SELECT last_seq FROM papuma.checkpoint
                WHERE handler_name = @name
                FOR UPDATE SKIP LOCKED
                """;
            lockCmd.Parameters.AddWithValue("name", handler.Name);
            checkpoint = await lockCmd.ExecuteScalarAsync(ct) as long?;
        }

        if (checkpoint is null)
        {
            return 0; // locked by another instance (or not yet registered)
        }

        var batch = await LoadBatchAsync(conn, tx, handler.Name, checkpoint.Value, ct);
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

            if (item.RetryPending)
            {
                break; // stop-the-line: strict ordering, retry after backoff (ADR-009)
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

        if (newCheckpoint != checkpoint.Value)
        {
            await SaveCheckpointAsync(conn, tx, handler.Name, newCheckpoint, ct);
        }

        await tx.CommitAsync(ct);
        return processed;
    }

    // RetryPending is decided by the database clock that also set next_retry_at — comparing it
    // with the application's clock would shift every retry by the clock skew between hosts.
    private sealed record BatchItem(ChangeRecord Record, int Attempts, bool RetryPending);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string handlerName, long checkpoint, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Stable visibility: a change only counts once no concurrently running transaction
        // could still commit an earlier seq (ADR-010).
        cmd.CommandText = """
            SELECT c.seq, c.scope, c.tenant_id, c.document_type, c.document_id, c.version,
                   c.schema_version, c.operation, c.diff::text, c.actor_id, c.metadata::text,
                   c.occurred_at, COALESCE(f.attempts, 0),
                   COALESCE(f.next_retry_at > now(), false) AS retry_pending
            FROM papuma.change c
            LEFT JOIN papuma.failure f
                   ON f.handler_name = @name AND f.seq = c.seq
            WHERE c.seq > @checkpoint
              AND c.txid < pg_snapshot_xmin(pg_current_snapshot())
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
                Operation: (ChangeOperation)reader.GetInt16(7),
                Diff: DocumentDiff.FromJson((JsonObject)JsonNode.Parse(reader.GetString(8))!),
                ActorId: reader.GetString(9),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(10))!,
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(11));

            batch.Add(new BatchItem(
                record,
                reader.GetInt32(12),
                reader.GetBoolean(13)));
        }

        return batch;
    }

    private static async Task SaveCheckpointAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string handlerName, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE papuma.checkpoint
            SET last_seq = @seq, updated_at = now()
            WHERE handler_name = @name
            """;
        cmd.Parameters.AddWithValue("name", handlerName);
        cmd.Parameters.AddWithValue("seq", seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> RegisterFailureAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string handlerName, long seq, Exception ex, CancellationToken ct)
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
        cmd.Parameters.AddWithValue("name", handlerName);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("error", FeedDiagnostics.SanitizeError(ex));
        cmd.Parameters.AddWithValue("baseDelay", _options.BaseRetryDelay.TotalSeconds);
        cmd.Parameters.AddWithValue("maxDelay", _options.MaxRetryDelay.TotalSeconds);

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ClearFailureAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string handlerName, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM papuma.failure WHERE handler_name = @name AND seq = @seq";
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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var handler in _handlers)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO papuma.checkpoint (handler_name, last_seq)
                VALUES (@name, 0)
                ON CONFLICT (handler_name) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("name", handler.Name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _registered = true;
    }
}
