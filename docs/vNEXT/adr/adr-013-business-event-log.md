# ADR-013: Domain events — translator, event log and the boundary between them

## Status

Accepted (2026-06-11)

## Context

ADR-011 keeps domain events out of the storage layer: the kernel only knows
`DocumentChanged`. But that covers only events that are **state transitions**
(`OrderPaid` from `status: Pending → Paid`). There is a second category: **facts
without state truth** — `UserLoggedIn`, `EmailSent`, `ExportDownloaded`. For
them, no diff exists from which a translator could derive anything; the fact
itself is the information (audit, fraud detection, behavioral analysis).

v1 already separated `change_feed` and `business_event_log` for this — that
separation returns as a deliberate vNEXT concept.

## Decision

Domain events are modeled according to three cases:

| Case | Example | Modeling |
|------|---------|----------|
| **State transition** | `OrderPlaced`, `OrderPaid` | The document change is the truth; a translator handler derives the event from the diff (ADR-011). Retroactively producible via rebuild. |
| **Fact without state** | `UserLoggedIn`, `EmailSent` | Explicit `session.Append(...)` into the **append-only event log** (below). |
| **Trigger** ("do X afterwards") | Confirmation email after an order | No stored event — a handler subscription on case 1 or 2 (ADR-009). |

Decision rule: *Must the system remember a state → document. Must it remember an
occurrence → event log. Should something merely happen → handler.*

### The event log

1. **Its own table**, same infrastructure patterns as the change feed:

   ```sql
   CREATE TABLE papuma.event
   (
       seq         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
       tenant_id   text        NOT NULL,
       event_type  text        NOT NULL,
       payload     jsonb       NOT NULL,   -- policy-applied
       metadata    jsonb       NOT NULL,   -- correlationId, actor, ...
       occurred_at timestamptz NOT NULL DEFAULT now(),
       txid        xid8        NOT NULL DEFAULT pg_current_xact_id()
   );
   ```

2. **Append runs in the session transaction**: `session.Append(new UserLoggedIn(...))`
   commits atomically with any saves/patches of the same session — the fact and
   the state change (e.g. a `lastLoginAt` patch) are never inconsistent and share
   the `correlationId`.
3. **Event types are registered C# types in the metamodel** — so the privacy
   policies (ADR-007) apply here too: `[SensitiveData]` on an IP address acts on
   the payload just as it does on a diff. Schema evolution follows the additive
   rules of ADR-005; transforming changes require a new event type (events are
   immutable facts; there is no upcasting when reading old events).
4. **Consumption via the same processing engine** (ADR-009/010): handlers
   subscribe to the change feed, the event log, or both; checkpoints, retry,
   snapshot-based polling and wakeup work identically (own checkpoint position per
   feed). There is **no global order across the two feeds** — whoever needs
   relationships correlates via `correlationId`.
5. **Retention is legitimate here**: unlike ChangeRecords (bound to document
   versions), events may be deleted after type-specific periods (`UserLoggedIn`
   after 90 days). The event log is a fact store, not a version store.

### The red line

The event log is **never a replay source for state**. No document is
reconstructed from events; no upcaster, no aggregate rebuild depends on it.
Whoever wants to go there wants event sourcing — and with it a different product
(cf. ADR-002).

## Consequences

- `UserLoggedIn`-style facts have a first-class place without bending the
  document model (no abuse of documents as event containers, no version explosion
  through high-frequency pseudo-patches).
- ADR-011 stays untouched: the kernel still interprets nothing — in case 1 the
  processing layer derives, in case 2 the application states the fact explicitly.
- Two feeds mean two checkpoint spaces; that is deliberately simpler than a
  unified sequence (v1's `kind` column in a unified feed), but costs global
  ordering between changes and events — accepted, correlation over ordering.
- Case-1 events remain the first choice wherever a state transition exists: they
  are retroactively producible and cannot be forgotten (the diff always arises);
  `Append`, by contrast, can be forgotten by a developer — a code-review topic.
