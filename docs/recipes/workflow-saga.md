# Recipe: Workflows and sagas on kernel primitives

Status: verified against running code (2026-06-12) — everything in this recipe
exists as an executable sample in
[samples/shop-minimal-api](../../samples/shop-minimal-api/README.md).
Background: [concepts §18](../concepts.md) ("waiting is state, not a thread").

The kernel ships no workflow engine — deliberately. This recipe shows why it
does not need one for the common cases: durable state, reactive feeds, atomic
transitions and a complete audit log are already primitives. The workflow
*definition* stays application code.

## The four mappings

| Workflow need | Kernel primitive | In the sample |
|---|---|---|
| Durable state machine per instance | a document (`status` + data) | [`Order`](../../samples/shop-minimal-api/Domain.cs) |
| React to facts | change/event feed handlers | [`OrderWorkflowHandler`](../../samples/shop-minimal-api/Handlers/OrderWorkflowHandler.cs) |
| Race-safe transitions | `expectedVersion` on the instance write | the decide endpoint + the handler |
| Execution log | the instance's change feed | `GET /orders/{id}/history` |

## Human-in-the-loop: materialize the question, never wait

The hard rule (stop-the-line, concepts §4): **a feed handler never waits for a
human** — it would halt its feed and end up poisoned. Instead, the handler turns
"a human must decide" into a *document* and returns:

```csharp
// OrderWorkflowHandler, step 1 — deterministic id makes redelivery harmless:
var task = new ApprovalTask(
    Id: $"approval-{change.DocumentId}",
    OrderId: change.DocumentId,
    Status: ApprovalStatus.Pending,
    RequestedAt: change.OccurredAt,
    DueAt: change.OccurredAt.AddMinutes(2));
try
{
    await session.SaveAsync(task, expectedVersion: 0, ct);
    await session.CommitAsync(ct);
}
catch (ConcurrencyException) { /* at-least-once redelivery: task exists */ }
```

The checkpoint advances; the waiting lives in the store — crash-safe and
arbitrarily long. The human decision is a perfectly normal write
(`PatchAsync` with `expectedVersion` — two simultaneous approvers serialize
typed), and *that* write produces the change the handler reacts to next:

```text
order insert ──► handler creates ApprovalTask ──► human patches the task
      ▲                                                    │
      └──────── handler patches the order ◄────────────────┘
```

## Transitions: load, decide, write with expectedVersion

The handler is a pure transition function around a guarded write. Three rules
make it correct under at-least-once delivery:

1. **Check the current state first** — if the transition already happened,
   return (idempotency).
2. **Write with `expectedVersion`** — a parallel trigger loses cleanly.
3. **On `ConcurrencyException`, just rethrow** — the engine retries with
   backoff, and the retry re-evaluates the *new* state. The retry mechanism of
   the feed engine *is* the workflow's conflict resolution.

## Compensation: the saga rollback is a normal write too

A rejected order gives its reserved stock back — transition and compensation in
one atomic session (sample, step 2):

```csharp
await session.SaveAsync(order.Document with { Status = OrderStatus.Rejected }, order.Version, ct);
await session.PatchAsync<Inventory>(order.Document.ProductId,
    p => p.Increment(x => x.Stock, order.Document.Quantity), ct: ct);
await session.CommitAsync(ct);
```

`Increment` is the sanctioned counter write (concepts §17) — no read-modify-write
window, and the inventory's change feed records the compensation as part of the
gapless stock ledger.

## Timers: the one missing primitive, and its small recipe

"Escalate if nobody decides in time" needs a clock — the kernel deliberately has
none. The recipe is a `dueAt` field plus a small hosted service
([`ApprovalEscalationService`](../../samples/shop-minimal-api/Services/ApprovalEscalationService.cs)):

1. **Find due instances** with a read-only SQL lens over the store (explicit
   scope predicates; the caveats of concepts §16 apply):

   ```sql
   SELECT id, version FROM papuma.document
   WHERE scope = @scope AND tenant_id = @tenantId
     AND document_type = 'ApprovalTask'
     AND data ->> 'status' = 'Pending'
     AND (data ->> 'dueAt')::timestamptz < now()
   ```

2. **Turn "time passed" into a normal write**: patch the task to `Escalated`
   with the version read in step 1 as `expectedVersion`. If a human decided in
   between, the poller's write fails typed and the human wins — exactly right.

From there the ordinary handler chain takes over (notify someone, reassign,
auto-reject after a second deadline — all more documents and handlers). For
multi-instance deployments, wrap the poll in a leader lock
(`FOR UPDATE SKIP LOCKED`, concepts §11).

## What you get for free

- **The execution log**: every transition with actor, timestamp, correlation
  and diff — `GET /orders/{id}/history` in the sample shows the insert by the
  customer and the approval by the manager, with the customer's email
  policy-redacted (ADR-007).
- **Crash safety**: kill the process anywhere in the chain — checkpoints resume
  the feed, the task document is the waiting state, deterministic ids absorb
  redeliveries.
- **Observability**: workflow throughput and stalls are feed lag and failure
  metrics (observability.md) — no separate workflow monitoring.

## Boundaries

No BPMN, no DSL, no designer, no versioned workflow *definitions* — when the
process logic changes, that is a code deployment, and in-flight instances follow
the new logic on their next transition (model breaking shape changes with the
usual schema-evolution tools, ADR-005). If you need definition versioning,
visual modeling or cross-service orchestration, put a dedicated engine *next to*
the kernel — the boundary stays: mechanisms below, decisions above (ADR-015).
