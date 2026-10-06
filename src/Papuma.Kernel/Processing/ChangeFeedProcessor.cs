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
/// The change feed engine (ADR-009/010/022): delivers committed changes to registered
/// handlers in causal order with persisted checkpoints, retry with exponential backoff,
/// poison skipping, and rebuild support.
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot cursor (ADR-022):</b> a handler's position is a transaction snapshot.
/// Each cycle delivers the transactions committed since it, whole and in <c>seq</c>
/// order within one slice — a change committed late can never land behind the cursor,
/// and an open transaction holds back only its own changes. Per document, delivery is
/// strictly by version.
/// </para>
/// <para>
/// <b>Leader coordination:</b> the per-handler checkpoint row is taken with
/// <c>FOR UPDATE SKIP LOCKED</c>; concurrent processor instances simply skip handlers
/// another instance is currently working on — no distributed consensus (ADR-010).
/// </para>
/// <para>
/// <b>Delivery:</b> at-least-once, stop-on-failure per handler (ordering kept); after
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
    private readonly Dictionary<string, int?> _projectionVersions;
    private readonly ConcurrentDictionary<string, bool> _pauseLogged = new(StringComparer.Ordinal);
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

        _projectionVersions = _handlers.ToDictionary(
            h => h.Name, h => HandlerKind.Validate(h, h.Name), StringComparer.Ordinal);

        _meter = new Meter(KernelDiagnostics.SourceName);
        _meter.CreateObservableGauge(
            "papuma.feed.lag",
            ObserveLag,
            unit: "{record}",
            description: "Committed records not yet delivered, per handler (ADR-022). " +
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

        await using var listen = new ListenConnection(_dataSource, _logger);
        await listen.TryOpenAsync(ct);

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
                    await listen.WaitAsync(_options.PollInterval, ct);
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
    /// cycle replays the full feed (rebuild, ADR-009). The raw tool: it does not empty the
    /// handler's target. For projections prefer <see cref="ResetProjectionsAsync"/>, which
    /// calls <see cref="IProjection.ResetAsync"/> first; never reset an effect handler.
    /// </summary>
    /// <param name="handlerName">The handler name.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ProjectionLifecycle.ResetCursorAsync(conn, tx, handlerName, version: null, ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("Checkpoint for handler {Handler} reset — full replay on next cycle.", handlerName);
    }

    /// <summary>
    /// Rebuilds every projection (<see cref="IProjection"/>) this processor runs: empties
    /// its target via <see cref="IProjection.ResetAsync"/>, then resets its checkpoint and
    /// failures — the next cycles replay the feed into it (ADR-024). Effect handlers are
    /// left alone; a projection a newer instance owns (higher stored version) is skipped.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The number of projections reset.</returns>
    public async Task<int> ResetProjectionsAsync(CancellationToken ct = default)
    {
        await EnsureRegisteredAsync(ct);

        var reset = 0;
        foreach (var handler in _handlers)
        {
            if (handler is IProjection && await ResetProjectionAsync(handler.Name, ct))
            {
                reset++;
            }
        }

        return reset;
    }

    /// <summary>
    /// Rebuilds one projection: empties its target via <see cref="IProjection.ResetAsync"/>,
    /// then resets its checkpoint and failures (ADR-024).
    /// </summary>
    /// <param name="handlerName">The projection's handler name.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>false</c> when a newer instance owns the projection (higher stored version).</returns>
    /// <exception cref="ArgumentException">No handler of that name, or it is not a projection.</exception>
    public async Task<bool> ResetProjectionAsync(string handlerName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        var handler = _handlers.FirstOrDefault(h => h.Name == handlerName)
            ?? throw new ArgumentException($"No handler named {handlerName} runs in this processor.", nameof(handlerName));
        if (handler is not IProjection projection || _projectionVersions[handlerName] is not int declared)
        {
            throw new ArgumentException(
                $"Handler {handlerName} is not a projection (IProjection) — resetting it would repeat its effects.",
                nameof(handlerName));
        }

        await EnsureRegisteredAsync(ct);
        return await ProjectionLifecycle.ResetProjectionAsync(_dataSource, handlerName, projection, declared, _logger, ct);
    }

    /// <summary>
    /// Returns a lag snapshot per handler: committed changes not yet delivered (ADR-022).
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
            headCmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM papuma.change";
            latestSeq = (long)(await headCmd.ExecuteScalarAsync(ct))!;
        }

        // Lag = committed rows not yet delivered (ADR-022), counted — a seq difference has
        // no meaning once delivery follows causal order.
        var snapshots = new List<ChangeFeedLagSnapshot>(_handlers.Count);
        foreach (var handler in _handlers)
        {
            var cursor = await SnapshotCursor.ReadOrInitialAsync(conn, tx, handler.Name, ct);
            var lag = await cursor.CountUndeliveredAsync(conn, tx, "papuma.change", ct);

            var declared = _projectionVersions[handler.Name];
            snapshots.Add(new ChangeFeedLagSnapshot(handler.Name, cursor.LastSeq, latestSeq, lag)
            {
                ProjectionVersion = declared,
                Paused = declared is int d && cursor.ProjectionVersion > d,
            });
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
        var locked = await SnapshotCursor.LockAsync(conn, tx, handler.Name, ct);
        if (locked is null)
        {
            return 0; // locked by another instance (or not yet registered)
        }

        if (IsPaused(handler, locked.ProjectionVersion))
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var cursor = await locked.WithSliceAsync(conn, tx, "papuma.change", ct);
        var batch = cursor.SliceSnapshot is null ? [] : await LoadBatchAsync(conn, tx, handler.Name, cursor, ct);
        if (batch.Count == 0 && locked.SliceSnapshot is not null)
        {
            // The slice in progress has run dry, so it is complete — go straight on with a
            // fresh one: a drained slice must not make the cycle look idle while newer
            // commits wait.
            cursor = await cursor.After(cursor.SliceSeq, 0, sliceCompleted: true).WithSliceAsync(conn, tx, "papuma.change", ct);
            batch = cursor.SliceSnapshot is null ? [] : await LoadBatchAsync(conn, tx, handler.Name, cursor, ct);
        }

        if (batch.Count == 0)
        {
            // Nothing to deliver. Keep a completed slice; drop an empty fresh one — the next
            // cycle takes a newer snapshot anyway, and an idle cycle then writes nothing.
            var idle = cursor with { SliceSnapshot = null, SliceSeq = 0 };
            if (idle != locked)
            {
                await idle.SaveAsync(conn, tx, handler.Name, ct);
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
                // Poison: skip, keep the failure entry as the permanent record.
                _logger.LogError(
                    "Handler {Handler} skips poison change seq {Seq} after {Attempts} attempts.",
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
                stopped = true;
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

            position = item.Record.Seq;
            highest = Math.Max(highest, item.Record.Seq);
            processed++;
        }

        // A batch that came back short without stopping the line has emptied the slice.
        var sliceCompleted = !stopped && batch.Count < _options.BatchSize;
        var next = cursor.After(position, highest, sliceCompleted);
        if (next != locked)
        {
            await next.SaveAsync(conn, tx, handler.Name, ct);
        }

        await tx.CommitAsync(ct);
        return processed;
    }

    // RetryPending is decided by the database clock that also set next_retry_at — comparing it
    // with the application's clock would shift every retry by the clock skew between hosts.
    private sealed record BatchItem(ChangeRecord Record, int Attempts, bool RetryPending);

    private async Task<IReadOnlyList<BatchItem>> LoadBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string handlerName, SnapshotCursor cursor, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // The next rows of the cursor's slice: transactions committed since the done
        // snapshot, whole, in seq order (ADR-022).
        var slice = cursor.SlicePredicate(cmd, "c");
        cmd.CommandText = $"""
            SELECT c.seq, c.scope, c.tenant_id, c.document_type, c.document_id, c.version,
                   c.schema_version, c.operation, c.diff::text, c.actor_id, c.metadata::text,
                   c.occurred_at, COALESCE(f.attempts, 0),
                   COALESCE(f.next_retry_at > now(), false) AS retry_pending
            FROM papuma.change c
            LEFT JOIN papuma.failure f
                   ON f.handler_name = @name AND f.seq = c.seq
            WHERE {slice}
            ORDER BY c.seq
            LIMIT @batchSize
            """;
        cmd.Parameters.AddWithValue("name", handlerName);
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

        // Before the first delivery: new checkpoints, and projection versions reconciled
        // (record, rebuild once, or pause for a newer instance — ADR-024).
        foreach (var handler in _handlers)
        {
            await ProjectionLifecycle.RegisterAsync(
                _dataSource, handler.Name, "papuma.change", handler, _projectionVersions[handler.Name], _logger, ct);
        }

        _registered = true;
    }

    // An instance declaring a lower version than the stored one runs older code: it must
    // not write the old shape into the target a newer instance rebuilt (ADR-024).
    private bool IsPaused(IChangeHandler handler, int? storedVersion)
    {
        if (_projectionVersions[handler.Name] is not int declared || storedVersion is not int stored || stored <= declared)
        {
            return false;
        }

        if (_pauseLogged.TryAdd(handler.Name, true))
        {
            _logger.LogWarning(
                "Projection {Handler} is at version {Stored}, this instance declares {Declared} — paused until this instance is replaced.",
                handler.Name, stored, declared);
        }

        return true;
    }
}
