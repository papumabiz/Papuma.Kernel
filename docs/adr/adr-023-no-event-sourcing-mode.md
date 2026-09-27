# ADR-023 — Event sourcing is not offered as an alternative storage mode

## Status

Accepted (2026-09-27) — confirms ADR-002 and the red line of ADR-013; a
kernel-level command log and a stream-id column on the event log are deferred
against the triggers below

## Context

The question comes back in two shapes: "rebuild the kernel on event sourcing",
or, more modestly, "offer event sourcing as an opt-in mode per document type"
(`.EventSourced<Account>(fold)`). The motivation is real — some aggregates *are*
streams (ledgers, account movements, meter readings), and some teams want the
intent of a change recorded, not just its diff.

Almost every kernel mechanism rests on "the document is the truth" (ADR-002):

| Mechanism | What an event-sourced mode would do to it |
|---|---|
| GDPR erasure as an `UPDATE`, history redaction (ADR-007, ADR-015) | Collides with immutable streams; needs crypto-shredding or stream rewriting — the crutch ADR-002 set out to avoid |
| Derived change feed via `RETURNING OLD, NEW` (ADR-003, ADR-004) | Redundant or duplicated: the events *are* the feed |
| Patch, bulk, rollback-as-update (ADR-008, ADR-012, ADR-014) | Each needs a command → event → fold equivalent |
| Additive schema evolution (ADR-005) | Event types need upcasters, which ADR-013 rules out for the event log |
| Scope predicates / RLS (ADR-019) | Needed on streams *and* on every fold result |

ADR-013 already drew the line: *"The event log is never a replay source for
state."* An opt-in mode would cross it inside the same package.

## Decision

1. **No event-sourced storage mode** in `Papuma.Kernel` or `Papuma.Kernel.Local`
   — neither globally nor per document type. The kernel never folds events into
   state, and no API accepts an `evolve`/`apply` function.
2. **Stream-shaped aggregates are an application pattern**, documented in the
   [stream-shaped aggregates recipe](../recipes/stream-shaped-aggregates.md): the
   document holds the state, each domain occurrence is appended to the event log
   in the same session, and the application's `evolve` runs exactly once — on
   write, not on load. Order is mandatory: `SaveAsync` (concurrency check) first,
   `AppendAsync` second. The facts can be *reconciled* against the state; they
   are never used to *rebuild* it.
3. **Recording command intent needs no kernel change.** `causationType`/
   `causationId` (ADR-018) name the command; where the payload matters, the
   application registers the command (or its outcome) as an event type and
   appends it (ADR-013 case 2). A generic kernel-level command log is **deferred**
   (trigger below).
4. **Event-as-truth domains use a dedicated store** (Marten, EventStoreDB) for
   that bounded context and integrate with Papuma through the feeds
   ([feed-wire-format](../feed-wire-format.md), bus bridges). The Event Modeling
   recipe's boundary section stays the guidance for when that is the case.

## Consequences

### Positive

- One write path, one consistency model, one answer to "how is personal data
  erased" — every guarantee in ADR-003 through ADR-022 keeps holding without an
  "except in ES mode" clause.
- Stream-shaped aggregates keep the document-sourced benefits: a direct load, no
  snapshots, no upcasters, and policies applying to the fact payloads.
- The product boundary stays legible: "event-sourcing benefits without the
  mandate" is the claim, and it stays true.

### Negative

- Teams that want a real event store must run a second store and integrate it —
  more infrastructure than an opt-in flag would have been.
- The stream-shaped pattern relies on discipline the kernel cannot enforce:
  save-before-append ordering, and `evolve` staying consistent with the facts. A
  drift is detectable (reconciliation), not preventable.
- No retroactive state: if `evolve` was wrong, correcting past states means a
  data migration on documents, not a replay. That is the ES capability given up.
- The event log has no stream-id column; reading one aggregate's stream means a
  payload predicate or an application projection (deferred, see below).

## Alternatives considered

- **Opt-in ES mode per document type** — rejected: it forks every mechanism in
  the Context table, and the resulting feature matrix ("policies apply, except…")
  costs more than the pattern saves.
- **Event log as an optional replay source** ("rebuild documents from events if
  asked") — rejected: two sources of truth that can disagree, and replay forces
  upcasters onto event types — both explicitly excluded by ADR-013.
- **A bundled Marten/EventStoreDB integration package** — rejected: a dependency
  the kernel does not need; the feed contract already lets any store integrate,
  and the choice of ES store belongs to the application.
- **A generic `CommandIssued` log in the kernel** — deferred, not rejected. It
  saves little over appending an application event type today.
  **Trigger:** two consumer applications carrying the same hand-written
  command-log event type, or a feedback entry that shows the code it would
  replace.
- **An optional stream-id column on `papuma.event`** (`AppendAsync(event,
  streamId)`, indexed per scope/tenant/stream) — deferred, not rejected. It would
  make "all facts of aggregate X" a kernel-supported, indexed read without a
  projection. Against it for now: it is a schema change on a kernel table, it
  touches the wire format (feed-wire-format.md) and `Papuma.Kernel.Local`, and
  it invites treating the event log as a per-aggregate stream store — one step
  from replay. The statement projection covers the need today and is usually the
  shape the UI wants anyway.
  **Trigger:** two consumer applications that build an event projection whose
  only purpose is lookup by aggregate id (no reshaping, no aggregation), or a
  measured payload-predicate query on `papuma.event` that exceeds its latency
  budget and cannot be moved to a projection.

## Related

- [ADR-002](adr-002-document-as-truth.md) — document as truth; this ADR keeps it unconditional
- [ADR-011](adr-011-no-business-events-in-storage.md) — no domain events in storage
- [ADR-013](adr-013-business-event-log.md) — the event log and its red line, which this ADR confirms
- [ADR-018](adr-018-causation-type-metadata.md) — causation type as the lightweight intent record
- [Recipe: stream-shaped aggregates](../recipes/stream-shaped-aggregates.md)
- [Recipe: Event Modeling slices](../recipes/event-modeling-slices.md) — when real ES is the truer fit
- [concepts §33](../concepts.md) — the explainer
