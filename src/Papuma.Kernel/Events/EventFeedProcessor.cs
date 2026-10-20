// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

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
    private readonly EventFeedEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventFeedProcessor"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="handlers">
    /// The event handlers: unique names of letters, digits, <c>.</c>, <c>_</c> and <c>-</c> (at most 100
    /// characters, starting with a letter or digit) — the name is the handler's checkpoint identity.
    /// </param>
    /// <exception cref="ArgumentException">A handler name is missing, invalid or used twice.</exception>
    /// <param name="options">Engine options (optional; shared shape with the change feed engine).</param>
    /// <param name="logger">Logger (optional).</param>
    public EventFeedProcessor(
        NpgsqlDataSource dataSource,
        IEnumerable<IEventHandler> handlers,
        ChangeFeedProcessorOptions? options = null,
        ILogger<EventFeedProcessor>? logger = null)
    {
        _engine = new EventFeedEngine(
            dataSource, handlers, options, logger ?? NullLogger<EventFeedProcessor>.Instance);
    }

    /// <summary>Disposes the per-instance lag gauge.</summary>
    public void Dispose() => _engine.Dispose();

    /// <summary>Gets a value indicating whether there is anything for <see cref="RunAsync"/> to run.</summary>
    internal bool HasHandlers => _engine.HasHandlers;

    /// <summary>
    /// Runs the processing loop until cancellation (NOTIFY wakeup, polling as truth — ADR-010).
    /// </summary>
    /// <param name="ct">The cancellation token stopping the loop.</param>
    public Task RunAsync(CancellationToken ct) => _engine.RunAsync(ct);

    /// <summary>
    /// Runs one processing cycle over all handlers and returns the number of delivered events.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="AggregateException">More than one handler's batch faulted (the others still ran).</exception>
    public Task<int> ProcessOnceAsync(CancellationToken ct = default) => _engine.ProcessOnceAsync(ct);

    /// <summary>
    /// Returns the persisted failure entries (retrying and poison) of this processor's
    /// handlers — the failure table as an API (phase 11 diagnostics).
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(CancellationToken ct = default) =>
        _engine.GetFailuresAsync(ct);

    /// <summary>
    /// Removes a failure entry so the next cycle retries the event immediately — the manual
    /// override for an event that is waiting out its backoff.
    /// </summary>
    /// <remarks>
    /// This does <b>not</b> redeliver a poison event (<c>attempts</c> reached
    /// <see cref="ChangeFeedProcessorOptions.MaxAttempts"/>): the checkpoint has already moved past
    /// it, or does at the next cycle. Removing the entry only deletes the permanent trace of the
    /// skip. To replay such an event into a projection, remove the entry and then rebuild the
    /// handler (<c>ResetProjectionAsync</c>) — without removing it the replay skips the event
    /// as poison again. An effect handler is never reset: repeat its lost work by hand (ADR-024).
    /// </remarks>
    /// <param name="handlerName">The handler name (without the internal prefix).</param>
    /// <param name="seq">The event log sequence number.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>true</c> when a failure entry existed and was removed.</returns>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor.</exception>
    public Task<bool> RetryFailureAsync(string handlerName, long seq, CancellationToken ct = default) =>
        _engine.RetryFailureAsync(handlerName, seq, ct);

    /// <summary>
    /// Resets a handler's checkpoint to 0 and clears its failure entries — full replay
    /// on the next cycle. The raw tool: it does not empty the handler's target. For
    /// projections prefer <see cref="ResetProjectionsAsync"/>; never reset an effect handler.
    /// </summary>
    /// <param name="handlerName">The handler name (without the internal prefix).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ArgumentException">No handler of that name runs in this processor.</exception>
    public Task ResetCheckpointAsync(string handlerName, CancellationToken ct = default) =>
        _engine.ResetCheckpointAsync(handlerName, ct);

    /// <summary>
    /// Rebuilds every projection (<see cref="IProjection"/>) this processor runs: empties
    /// its target via <see cref="IProjection.ResetAsync"/>, then resets its checkpoint and
    /// failures (ADR-024). Effect handlers are left alone; a projection a newer instance
    /// owns (higher stored version) is skipped.
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
    /// Returns a lag snapshot per handler: committed events not yet delivered (ADR-022).
    /// A pure read — it neither registers a handler nor reconciles a projection version, so a
    /// health probe, the dashboard or an MCP tool on a node that runs no workers can never
    /// trigger a rebuild. A handler that has not been registered yet shows the whole backlog.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<ChangeFeedLagSnapshot>> GetLagAsync(CancellationToken ct = default) =>
        _engine.GetLagAsync(ct);

    /// <summary>What the event feed adds to the shared engine: its table, key space and row mapping.</summary>
    private sealed class EventFeedEngine(
        NpgsqlDataSource dataSource,
        IEnumerable<IEventHandler> handlers,
        ChangeFeedProcessorOptions? options,
        ILogger logger)
        : PostgresFeedEngine<IEventHandler, EventRecord>(dataSource, handlers, options, logger, nameof(handlers))
    {
        protected override string FeedTag => "event";

        protected override string FeedName => "Event";

        protected override string Table => "papuma.event";

        protected override string CheckpointPrefix => "event:";

        protected override string Alias => "e";

        protected override string RecordColumns => """
            e.seq, e.scope, e.tenant_id, e.event_type, e.payload::text, e.actor_id,
            e.metadata::text, e.occurred_at
            """;

        protected override int RecordColumnCount => 8;

        protected override string NameOf(IEventHandler handler) => handler.Name;

        protected override Task HandleAsync(IEventHandler handler, EventRecord record, CancellationToken ct) =>
            handler.HandleAsync(record, ct);

        protected override long SeqOf(EventRecord record) => record.Seq;

        protected override JsonObject MetadataOf(EventRecord record) => record.Metadata;

        protected override EventRecord Map(NpgsqlDataReader reader)
        {
            var scope = reader.GetString(1) == nameof(ScopeType.Tenant)
                ? ScopeContext.Tenant(reader.GetString(2))
                : ScopeContext.Platform();

            return new EventRecord(
                Seq: reader.GetInt64(0),
                Scope: scope,
                EventType: reader.GetString(3),
                Payload: (JsonObject)JsonNode.Parse(reader.GetString(4))!,
                ActorId: reader.GetString(5),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(6))!,
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(7));
        }
    }
}
