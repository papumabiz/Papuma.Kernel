# ADR-009: Projections as dumb change handlers

## Status

Accepted (2026-06-11); ordering amended by [ADR-022](adr-022-snapshot-cursor.md) —
delivery follows causal order (per document strictly by version), not global `seq`
order.

## Context

CQRS/projection frameworks often abstract in the wrong place: the write side is
generic (worth abstracting), the read side almost never is — a search index, a
dashboard SQL statement and a reporting insert for the same change have nothing in
common. Read-model DSLs regularly end at "okay, I do need SQL after all".

## Decision

1. **One interface, nothing more:**

   ```csharp
   public interface IChangeHandler
   {
       string Name { get; }
       Task HandleAsync(ChangeRecord change, CancellationToken ct);
   }
   ```

   A handler can be a SQL projection, an Elasticsearch update, a webhook, a Kafka
   publish, an audit log or an event translator (ADR-011). The kernel generates no
   SQL and knows no read models.

2. **The engine delivers infrastructure only:**
   - ordering: per handler strictly by `seq` (and thus per document by `version`),
   - persisted checkpoints per handler (`papuma.checkpoint`),
   - retry with backoff, poison handling (skip the change + alarm instead of an
     endless loop),
   - rebuild: reset the checkpoint, replay the feed,
   - parallelization across handlers (never within one handler).

3. **At-least-once semantics.** Handlers must be idempotent; the engine provides
   `(handler, seq)` as the natural idempotency key for this.

4. **Convenience as sugar, not as a layer:** filter helpers such as

   ```csharp
   WhenFieldChanged<User>(x => x.Email)
   ```

   are thin wrappers over `ChangeRecord.Diff` and produce ordinary handlers.

## Consequences

- Projections use the best tool directly (SQL, client SDKs) — no leaky
  abstraction, no "yes, but this case is special".
- The kernel's quality is decided at checkpoints/retry/rebuild — exactly where the
  investment goes.
- Idempotency is a handler obligation and is backed in the docs with patterns
  (upsert, `ON CONFLICT`, checkpoint-comparing writes).
