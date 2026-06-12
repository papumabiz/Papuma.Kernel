# Recipe: The NATS bridge — publishing the feeds to JetStream

Status: verified against running code (2026-06-12) — the bridge lives in the
sample ([NatsBridge.cs](../../samples/Papuma.Kernel.Sample/Handlers/NatsBridge.cs),
NATS.Net 2.8.1) and was proven end-to-end including the dedup behavior below.
Background: [concepts §22](../concepts.md) ("the feed is the outbox").

## The shape: two dumb handlers, nothing else

The derived feed is already a transactional outbox — the ChangeRecord commits in
the same transaction as the document, so nothing can be forgotten and nothing
uncommitted can be published. The bus attachment therefore needs no new
machinery: it is an ordinary `IChangeHandler`/`IEventHandler` pair that inherits
ordering, checkpoints, retry, poison handling and lag metrics from the engine.

```csharp
public sealed class NatsChangePublisher(INatsJSContext jetStream) : IChangeHandler
{
    public string Name => "nats-change-publisher"; // own checkpoint, own lag metric

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        var subject = $"papuma.change.{change.Scope.TenantId}.{change.DocumentType}";
        var ack = await jetStream.PublishAsync(subject, Serialize(change),
            opts: new NatsJSPubOpts { MsgId = $"change-{change.Seq}" },
            cancellationToken: ct);

        if (!ack.Duplicate)
        {
            ack.EnsureSuccess(); // real failures throw → engine retry with backoff
        }
    }
}
```

Wiring (sample: optional via `Nats:Url`):

```csharp
builder.Services.AddSingleton(_ => new NatsConnection(new NatsOpts { Url = natsUrl }));
builder.Services.AddSingleton<INatsJSContext>(sp =>
    sp.GetRequiredService<NatsConnection>().CreateJetStreamContext());
builder.Services.AddHostedService<NatsStreamInitializer>(); // creates the stream idempotently
kernel.AddChangeHandler<NatsChangePublisher>()
      .AddEventHandler<NatsEventPublisher>();
```

The stream initializer declares one stream over `papuma.>` with a generous
duplicate window (10 min in the sample — handler redeliveries can arrive minutes
later thanks to backoff).

## The three load-bearing details

1. **The feed `seq` as `Nats-Msg-Id`.** The engine delivers at-least-once; the
   handler does not fight that — JetStream's dedup turns the redeliveries into
   no-ops. Verified live: after resetting the publisher's checkpoint to 0, all 7
   prior changes were redelivered and republished; the stream count did not move
   (10 messages instead of 17), the checkpoint advanced cleanly to the head,
   zero failure entries.
2. **A duplicate ack is success — do not `EnsureSuccess()` blindly.** NATS.Net
   throws `NatsJSDuplicateMessageException` from `EnsureSuccess()` on a deduped
   publish. Under at-least-once that is the *desired* outcome; treating it as an
   error sends the handler into stop-the-line backoff and ends in poison
   entries. Check `ack.Duplicate` first (see code above). This one bit us in
   verification — hence its own bullet.
3. **The subject hierarchy extends tenant isolation onto the transport.**
   `papuma.change.{tenant}.{documentType}` means NATS account/permission rules
   (`papuma.change.acme.>`) continue the scope model: a consumer structurally
   never sees foreign tenants. And because policies acted at write time
   (ADR-007), the bus cannot distribute sensitive values in the first place.

## Consumer side (any language)

JetStream consumers get the messages in stream order. For strictly ordered
processing use a durable consumer with `max_ack_pending = 1` (the bus-side
equivalent of "one consumer per handler", concepts §14); for parallel fan-out
relax it and key your idempotency on the `seq` field inside the payload — the
dedup window only protects the *publish* side, not redeliveries to consumers.

```bash
nats consumer add PAPUMA shop-sync --filter "papuma.change.demo.>" \
  --deliver all --ack explicit --max-pending 1
```

## Boundaries

- **The stream is a copy; the feed remains the truth and the replay source.**
  A rebuild is a checkpoint reset at the feed — never "rewind the bus". Bus
  retention is purely a transport decision.
- **Foreign consumers are consumers** (concepts §21): writes go through the
  Papuma application's API, never into `papuma.*` tables.
- For external contracts, consider publishing **translated domain events**
  (ADR-011: `OrderPaid` instead of raw document diffs) — same handler pattern,
  stabler surface. Raw-change subjects are ideal for infrastructure consumers
  (sync, search, cache); domain-event subjects for business consumers.

## Try it

```bash
docker run -d --name nats -p 4222:4222 -p 8222:8222 nats:latest -js -m 8222
Nats__Url=nats://127.0.0.1:4222 dotnet run --project samples/Papuma.Kernel.Sample
# place orders, then inspect: curl -s localhost:8222/jsz?streams=true | jq
```

(Note `127.0.0.1` rather than `localhost`: container ports are published on
IPv4, and a `localhost` that resolves to `::1` first refuses the connection.)
