// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Processing;

/// <summary>
/// The change feed engine for a single-writer embedded store — SQLite counterpart of
/// the Postgres kernel's <c>ChangeFeedProcessor</c> (referenced only in doc
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
/// <b>No LISTEN/NOTIFY:</b> replaced by an optional in-process <see cref="SqliteChangeNotifier"/>;
/// the processor waits on its own subscription.
/// </para>
/// <para>
/// <b>Handlers run with no transaction open.</b> A cycle reads checkpoint and batch in a
/// short transaction, runs the handlers, then records the outcome in a short write
/// transaction — only if the checkpoint is still the one read (a concurrent
/// <see cref="ResetCheckpointAsync"/> wins; the stale outcome is dropped). The database
/// file's single write lock is therefore free while handlers run: a projection writes to
/// the same file through its own connection, and a slow handler does not block the
/// application. Run one processor per database file (the hosted service does): nothing
/// serializes two instances any more, and a second one would deliver the same batch
/// twice — still at-least-once, but pointless work.
/// </para>
/// </remarks>
public sealed class SqliteChangeFeedProcessor : IDisposable
{
    private readonly ChangeFeedEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteChangeFeedProcessor"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="handlers">
    /// The change handlers: unique names of letters, digits, <c>.</c>, <c>_</c> and <c>-</c> (at most 100
    /// characters, starting with a letter or digit) — the name is the handler's checkpoint identity.
    /// </param>
    /// <exception cref="ArgumentException">A handler name is missing, invalid or used twice.</exception>
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
        _engine = new ChangeFeedEngine(
            connectionString, handlers, notifier, options, logger ?? NullLogger<SqliteChangeFeedProcessor>.Instance);
    }

    /// <summary>Disposes the per-instance lag gauge.</summary>
    public void Dispose() => _engine.Dispose();

    /// <summary>Gets a value indicating whether there is anything for <see cref="RunAsync"/> to run.</summary>
    internal bool HasHandlers => _engine.HasHandlers;

    /// <summary>
    /// Runs the processing loop until cancellation: process all handlers, then wait for
    /// an in-process wakeup or the poll interval.
    /// </summary>
    /// <param name="ct">The cancellation token stopping the loop.</param>
    public Task RunAsync(CancellationToken ct) => _engine.RunAsync(ct);

    /// <summary>
    /// Runs one processing cycle over all handlers and returns the number of delivered
    /// changes. Exposed for hosting integration and tests.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="AggregateException">More than one handler's batch faulted (the others still ran).</exception>
    public Task<int> ProcessOnceAsync(CancellationToken ct = default) => _engine.ProcessOnceAsync(ct);

    /// <summary>
    /// Resets a handler's checkpoint to 0 and clears its failure entries — the next
    /// cycle replays the full feed.
    /// </summary>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor (the event feed's handlers are not reachable here).</exception>
    public Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default) =>
        _engine.ResetCheckpointAsync(handlerName, ct);

    /// <summary>
    /// Returns a lag snapshot per handler (feed head vs. checkpoint). A pure read: it neither
    /// registers a handler nor reconciles a projection version, so a dashboard or health probe
    /// can never trigger a rebuild. A handler that has not been registered yet shows the whole backlog.
    /// </summary>
    public Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct = default) =>
        _engine.GetLagAsync(ct);

    /// <summary>
    /// Returns the persisted failure entries (retrying and poison) of this processor's
    /// handlers.
    /// </summary>
    public Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct = default) =>
        _engine.GetFailuresAsync(ct);

    /// <summary>
    /// Removes a failure entry so the next cycle retries the record immediately.
    /// </summary>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor (the event feed's handlers are not reachable here).</exception>
    public Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct = default) =>
        _engine.RetryFailureAsync(handlerName, seq, ct);

    /// <summary>
    /// Rebuilds every projection (<see cref="IProjection"/>) this processor runs: empties
    /// its target via <see cref="IProjection.ResetAsync"/>, then resets its checkpoint and
    /// failures (ADR-024). Effect handlers are left alone.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The number of projections reset.</returns>
    public Task<int> ResetProjectionsAsync(CancellationToken ct = default) => _engine.ResetProjectionsAsync(ct);

    /// <summary>
    /// Rebuilds one projection: empties its target via <see cref="IProjection.ResetAsync"/>,
    /// then resets its checkpoint and failures (ADR-024).
    /// </summary>
    /// <param name="handlerName">The projection's handler name.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>false</c> when a newer instance owns the projection (higher stored version).</returns>
    /// <exception cref="ArgumentException">No handler of that name, or it is not a projection.</exception>
    public Task<bool> ResetProjectionAsync(string handlerName, CancellationToken ct = default) =>
        _engine.ResetProjectionAsync(handlerName, ct);

    /// <summary>What the change feed adds to the shared engine: its table, key space and row mapping.</summary>
    private sealed class ChangeFeedEngine(
        string connectionString,
        IEnumerable<IChangeHandler> handlers,
        SqliteChangeNotifier? notifier,
        ChangeFeedProcessorOptions? options,
        ILogger logger)
        : SqliteFeedEngine<IChangeHandler, ChangeRecord>(
            connectionString, handlers, notifier, options, logger, nameof(handlers))
    {
        protected override string FeedTag => "change";

        protected override string FeedName => "Change";

        protected override string Table => "change";

        protected override string CheckpointPrefix => string.Empty;

        protected override string Alias => "c";

        protected override string RecordColumns => """
            c.seq, c.scope, c.tenant_id, c.document_type, c.document_id, c.version,
            c.schema_version, c.operation, c.diff, c.actor_id, c.metadata,
            c.occurred_at
            """;

        protected override int RecordColumnCount => 12;

        protected override string NameOf(IChangeHandler handler) => handler.Name;

        protected override Task HandleAsync(IChangeHandler handler, ChangeRecord record, CancellationToken ct) =>
            handler.HandleAsync(record, ct);

        protected override long SeqOf(ChangeRecord record) => record.Seq;

        protected override JsonObject MetadataOf(ChangeRecord record) => record.Metadata;

        protected override ChangeRecord Map(SqliteDataReader reader)
        {
            var scope = reader.GetString(1) == nameof(ScopeType.Tenant)
                ? ScopeContext.Tenant(reader.GetString(2))
                : ScopeContext.Platform();

            return new ChangeRecord(
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
                OccurredAt: SqliteTimestamp.Parse(reader.GetString(11)));
        }
    }
}
