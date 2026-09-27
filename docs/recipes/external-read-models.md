# Recipe: External read models — search engines, vector stores, caches

Status: pattern recipe (2026-06-12). Deliberately **one** recipe for Manticore,
Qdrant, Redis and friends: the kernel-side pattern is identical every time, and
what differs is only the client SDK — which this document will not duplicate.
Background: [ADR-009](../adr/adr-009-projections-as-dumb-handlers.md),
[concepts §19](../concepts.md) (rebuild), [§14](../concepts.md) (scaling),
[ai-consumers](ai-consumers.md) (the same-database variant with pgvector).

## The one pattern

```csharp
public sealed class SearchIndexProjection(DocumentStore store, ISearchClient search)
    : IChangeHandler
{
    public string Name => "manticore-products";   // checkpoint identity — never rename

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != "Product") return;

        if (change.Operation == ChangeOperation.Delete)
        {
            await search.DeleteAsync(DocKey(change), ct);   // idempotent: delete twice = fine
            return;
        }

        // The diff says THAT something relevant changed; the state comes fresh:
        if (!change.FieldChanged("name") && !change.FieldChanged("description")) return;

        await using var session = store.OpenSession(change.Scope);
        var product = await session.LoadAsync<Product>(change.DocumentId, ct);
        if (product is null || product.Version > change.Version) return; // gone, or a newer
            // change is already committed — its delivery comes next (per-document order) and projects it

        await search.UpsertAsync(DocKey(change), Map(product.Document), product.Version, ct);
    }

    private static string DocKey(ChangeRecord c) => $"{c.Scope.TenantId}:{c.DocumentId}";
}
```

Five load-bearing rules, all consequences of the target living **outside the
write transaction**:

1. **Upsert keyed by document id, guarded by version.** At-least-once delivery
   means the same change can arrive twice and an old change can arrive after you
   already wrote a newer state. Key on `{tenant}:{documentId}`, store the
   document `version` alongside, and skip/conditionally-write when the target
   already has something newer (the pgvector recipe shows the SQL variant:
   `ON CONFLICT … WHERE existing.version < @version`).
2. **Load the state, project the state — the diff is the trigger, not the
   payload.** The diff tells you cheaply *whether* the handler cares; the
   mapping reads the current document. This makes redeliveries and skipped
   intermediate versions harmless: you always project the latest truth.
3. **Deletes are part of the contract.** Every external projection needs the
   `Delete` branch; removing a missing key must be a no-op.
4. **The tenant belongs in the key (or the filter).** External systems know
   nothing about RLS — isolation must be re-established in the projection:
   tenant prefix in the document key, a tenant field as a mandatory filter, or
   one index/collection per tenant. (A table in the *same* PostgreSQL database
   can keep RLS instead — see [read models in the same database](same-database-read-models.md).)
5. **Rebuild = reset + replay, and it must be safe.** `ResetCheckpointAsync`
   replays everything (concepts §19); because of rules 1–3 that is idempotent.
   For a clean slate, truncate the external index first — it is a copy, never
   the truth.

Operationally, all of this is visible for free: the handler has its own
checkpoint, lag gauge and failure entries. A slow external system shows up as
`papuma.feed.handler.duration` before it becomes a problem (concepts §14).

## Mapping the pattern onto the usual suspects

| Target | Upsert | Notes that actually matter |
|---|---|---|
| **Manticore Search** | `REPLACE INTO products (id, …) VALUES …` on a real-time index | `REPLACE` is the idempotent upsert. Manticore wants integer ids — derive one (e.g. hash) but store `{tenant}:{documentId}` + `version` as attributes for filtering and the version guard. Tenant = mandatory attribute filter on every query. |
| **Qdrant** | `Upsert(points: [{ id, vector, payload }])` | Point id = deterministic UUID from `{tenant}:{documentId}` (Qdrant ids are UUID/uint). Put `tenantId` and `version` in the payload; enforce tenancy via payload filter on every search — or one collection per tenant for hard isolation. Embedding computation is the expensive part → see the pgvector recipe's notes on handler duration. |
| **Redis (cache)** | `SET key value` or `DEL key` | Decide **invalidate vs. materialize**: `DEL` on change (next reader re-fills from the store — simplest and self-healing) vs. writing the projected value (`SET` with the version inside; readers ignore stale). Always set a TTL — it turns every missed update into a bounded staleness window instead of a permanent lie. Key = `{tenant}:{documentType}:{documentId}`. |

The choice among them is a read-side product decision (ADR-009's whole point):
the kernel-side handler is the same ~30 lines every time.

## What about DotNetCore.CAP?

Short version: **on the publish side, CAP is redundant here.** CAP's core value
is the transactional outbox — a message table committed with your business write
plus a dispatcher with retries. Papuma's derived change feed *is* exactly that
(concepts §22), with stronger properties (gapless, replayable, policy-minimized,
versioned per document). Putting CAP behind a handler would stack a second
persistence layer, a second retry machinery and a second checkpoint regime on
top of the engine's — double the moving parts, zero additional guarantee.

Where CAP remains legitimate:

- **As the consumer framework in other .NET services**: services that consume
  your bus messages (NATS/RabbitMQ/Kafka) can use CAP's subscriber model,
  retries and dashboard — that is independent of Papuma.
- **As an existing organizational standard**: if every service in the house
  already publishes through CAP, a bridge handler may simply call
  `capPublisher.PublishAsync(...)` — functional, just aware that CAP's outbox
  feature is idle there (the handler is already behind the real outbox).

Decision rule: greenfield → publish straight to the bus from a handler
([nats-bridge](nats-bridge.md)); CAP only where it already owns the messaging
landscape.
