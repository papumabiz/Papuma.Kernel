// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Changes;
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

    private readonly ChangeFeedEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeFeedProcessor"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="handlers">
    /// The change handlers: unique names of letters, digits, <c>.</c>, <c>_</c> and <c>-</c> (at most 100
    /// characters, starting with a letter or digit) — the name is the handler's checkpoint identity.
    /// </param>
    /// <exception cref="ArgumentException">A handler name is missing, invalid or used twice.</exception>
    /// <param name="options">Engine options (optional).</param>
    /// <param name="logger">Logger (optional).</param>
    public ChangeFeedProcessor(
        NpgsqlDataSource dataSource,
        IEnumerable<IChangeHandler> handlers,
        ChangeFeedProcessorOptions? options = null,
        ILogger<ChangeFeedProcessor>? logger = null)
    {
        _engine = new ChangeFeedEngine(
            dataSource, handlers, options, logger ?? NullLogger<ChangeFeedProcessor>.Instance);
    }

    /// <summary>Disposes the per-instance lag gauge.</summary>
    public void Dispose() => _engine.Dispose();

    /// <summary>Gets a value indicating whether there is anything for <see cref="RunAsync"/> to run.</summary>
    internal bool HasHandlers => _engine.HasHandlers;

    /// <summary>
    /// Runs the processing loop until cancellation: process all handlers, then wait for
    /// a NOTIFY wakeup or the poll interval — polling stays the source of truth (ADR-010).
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
    /// cycle replays the full feed (rebuild, ADR-009). The raw tool: it does not empty the
    /// handler's target. For projections prefer <see cref="ResetProjectionsAsync"/>, which
    /// calls <see cref="IProjection.ResetAsync"/> first; never reset an effect handler.
    /// </summary>
    /// <param name="handlerName">The handler name.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor (the event feed's handlers are not reachable here).</exception>
    public Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default) =>
        _engine.ResetCheckpointAsync(handlerName, ct);

    /// <summary>
    /// Rebuilds every projection (<see cref="IProjection"/>) this processor runs: empties
    /// its target via <see cref="IProjection.ResetAsync"/>, then resets its checkpoint and
    /// failures — the next cycles replay the feed into it (ADR-024). Effect handlers are
    /// left alone; a projection a newer instance owns (higher stored version) is skipped.
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

    /// <summary>
    /// Returns a lag snapshot per handler: committed changes not yet delivered (ADR-022).
    /// A pure read — it neither registers a handler nor reconciles a projection version, so a
    /// health probe, the dashboard or an MCP tool on a node that runs no workers can never
    /// trigger a rebuild. A handler that has not been registered yet shows the whole backlog.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct = default) =>
        _engine.GetLagAsync(ct);

    /// <summary>
    /// Returns the persisted failure entries (retrying and poison) of this processor's
    /// handlers — the failure table as an API (phase 11 diagnostics).
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct = default) =>
        _engine.GetFailuresAsync(ct);

    /// <summary>
    /// Removes a failure entry so the next cycle retries the record immediately — the manual
    /// override for a record that is waiting out its backoff.
    /// </summary>
    /// <remarks>
    /// This does <b>not</b> redeliver a poison record (<c>attempts</c> reached
    /// <see cref="ChangeFeedProcessorOptions.MaxAttempts"/>): the checkpoint has already moved past
    /// it, or does at the next cycle. Removing the entry only deletes the permanent trace of the
    /// skip. To replay such a record into a projection, remove the entry and then rebuild the
    /// handler (<c>ResetProjectionAsync</c>) — without removing it the replay skips the record
    /// as poison again. An effect handler is never reset: repeat its lost work by hand (ADR-024).
    /// </remarks>
    /// <param name="handlerName">The handler name.</param>
    /// <param name="seq">The feed sequence number.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>true</c> when a failure entry existed and was removed.</returns>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor (the event feed's handlers are not reachable here).</exception>
    public Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct = default) =>
        _engine.RetryFailureAsync(handlerName, seq, ct);

    /// <summary>What the change feed adds to the shared engine: its table, key space and row mapping.</summary>
    private sealed class ChangeFeedEngine(
        NpgsqlDataSource dataSource,
        IEnumerable<IChangeHandler> handlers,
        ChangeFeedProcessorOptions? options,
        ILogger logger)
        : PostgresFeedEngine<IChangeHandler, ChangeRecord>(dataSource, handlers, options, logger, nameof(handlers))
    {
        protected override string FeedTag => "change";

        protected override string FeedName => "Change";

        protected override string Table => "papuma.change";

        protected override string CheckpointPrefix => string.Empty;

        protected override string Alias => "c";

        protected override string RecordColumns => """
            c.seq, c.scope, c.tenant_id, c.document_type, c.document_id, c.version,
            c.schema_version, c.operation, c.diff::text, c.actor_id, c.metadata::text,
            c.occurred_at
            """;

        protected override int RecordColumnCount => 12;

        protected override string NameOf(IChangeHandler handler) => handler.Name;

        protected override Task HandleAsync(IChangeHandler handler, ChangeRecord record, CancellationToken ct) =>
            handler.HandleAsync(record, ct);

        protected override long SeqOf(ChangeRecord record) => record.Seq;

        protected override JsonObject MetadataOf(ChangeRecord record) => record.Metadata;

        protected override ChangeRecord Map(NpgsqlDataReader reader)
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
                Operation: (ChangeOperation)reader.GetInt16(7),
                Diff: DocumentDiff.FromJson((JsonObject)JsonNode.Parse(reader.GetString(8))!),
                ActorId: reader.GetString(9),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(10))!,
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(11));
        }
    }
}
