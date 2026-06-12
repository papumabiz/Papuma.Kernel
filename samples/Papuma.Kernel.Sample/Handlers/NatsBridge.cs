// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Sample.Handlers;

// The NATS bridge (recipe: nats-bridge, concepts §22): the derived feed is already
// a transactional outbox, so the bus attachment is just another dumb handler —
// ordering, checkpoints, retry and poison handling come from the engine for free.
//
// Optional by configuration: set Nats:Url (e.g. nats://localhost:4222) and the
// sample registers the stream initializer and both publishers; without it, the
// sample runs unchanged.

/// <summary>
/// Creates the PAPUMA JetStream stream at startup (idempotent). The duplicate
/// window backs the seq-based dedup of the publishers below.
/// </summary>
public sealed class NatsStreamInitializer(INatsJSContext jetStream) : IHostedService
{
    public const string StreamName = "PAPUMA";

    public async Task StartAsync(CancellationToken cancellationToken) =>
        await jetStream.CreateStreamAsync(new StreamConfig(StreamName, subjects: ["papuma.>"])
        {
            // Within this window, JetStream drops re-publishes with a known
            // Nats-Msg-Id — generous, because handler redeliveries (backoff!)
            // can arrive minutes later.
            DuplicateWindow = TimeSpan.FromMinutes(10),
        }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Publishes every change record to <c>papuma.change.{tenant}.{documentType}</c>.
/// The subject hierarchy extends tenant isolation onto the transport: a consumer
/// permitted only <c>papuma.change.acme.&gt;</c> structurally never sees other
/// tenants. Payloads are policy-applied (ADR-007) — the bus cannot leak secrets.
/// </summary>
public sealed class NatsChangePublisher(INatsJSContext jetStream) : IChangeHandler
{
    public string Name => "nats-change-publisher"; // own checkpoint, own lag metric

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["seq"] = change.Seq,
            ["tenantId"] = change.Scope.TenantId,
            ["documentType"] = change.DocumentType,
            ["documentId"] = change.DocumentId,
            ["version"] = change.Version,
            ["operation"] = change.Operation.ToString(),
            ["diff"] = change.Diff.ToJson(),
            ["metadata"] = change.Metadata.DeepClone(),
            ["occurredAt"] = change.OccurredAt.ToString("O"),
        };

        var subject = $"papuma.change.{change.Scope.TenantId}.{change.DocumentType}";
        var ack = await jetStream.PublishAsync(subject, payload.ToJsonString(),
            // The feed seq as Nats-Msg-Id: at-least-once delivery from the engine
            // becomes effective exactly-once toward the bus (JetStream dedup).
            opts: new NatsJSPubOpts { MsgId = $"change-{change.Seq}" },
            cancellationToken: ct);

        // Careful: EnsureSuccess() throws NatsJSDuplicateMessageException for deduped
        // re-publishes — but under at-least-once, a duplicate ack IS success (the
        // message is already in the stream; that is the whole point of the MsgId).
        if (!ack.Duplicate)
        {
            ack.EnsureSuccess(); // real failures throw → engine retry with backoff
        }
    }
}

/// <summary>
/// Publishes event-log facts to <c>papuma.event.{tenant}.{eventType}</c> —
/// same mechanics, separate checkpoint space (ADR-013).
/// </summary>
public sealed class NatsEventPublisher(INatsJSContext jetStream) : IEventHandler
{
    public string Name => "nats-event-publisher";

    public async Task HandleAsync(EventRecord @event, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["seq"] = @event.Seq,
            ["tenantId"] = @event.Scope.TenantId,
            ["eventType"] = @event.EventType,
            ["payload"] = @event.Payload.DeepClone(),
            ["metadata"] = @event.Metadata.DeepClone(),
            ["occurredAt"] = @event.OccurredAt.ToString("O"),
        };

        var subject = $"papuma.event.{@event.Scope.TenantId}.{@event.EventType}";
        var ack = await jetStream.PublishAsync(subject, payload.ToJsonString(),
            opts: new NatsJSPubOpts { MsgId = $"event-{@event.Seq}" },
            cancellationToken: ct);
        if (!ack.Duplicate)
        {
            ack.EnsureSuccess(); // duplicate == success under at-least-once (see above)
        }
    }
}
