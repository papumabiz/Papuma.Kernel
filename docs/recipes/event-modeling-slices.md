# Recipe: Event Modeling slices on Papuma.Kernel

Status: guide (2026-06-13). Maps Adam Dymitruk's **Event Modeling** and the
**vertical-slice** style (Martin Dilger, *Understanding Eventsourcing*) onto
Papuma. Every pattern below is named against real code in the
[sample shop](../../samples/shop-minimal-api/README.md).

## Start here: Event Modeling is not Event Sourcing

The single most important thing to internalize — and Dymitruk says it himself:
**Event Modeling is a design method, not a persistence mandate.** It describes
how information flows over time (the swimlanes, the blue/orange/green boxes); how
you *store* that is a separate, free choice. You can realize an event model on
EventStoreDB, on plain CRUD, or on a document-sourced kernel like Papuma. The
blueprint does not change.

That is the permission you need: **model with Event Modeling, implement on
Papuma.** You only need to know where the "events" in the diagram land — and that
is what this guide makes precise.

## Why document-sourced is a good host — and where it's even better

Event Modeling's four patterns map onto Papuma primitives cleanly:

| Event Modeling pattern | Blueprint shape | Papuma realization | In the sample |
|---|---|---|---|
| **Command** | UI → command → event(s) | A command handler loads the document, decides, `Save`/`Patch`/`Append`. The change feed is derived automatically. | `POST /orders` (PlaceOrder), `POST /approvals/{id}/decide` (DecideApproval), `POST /products` |
| **View** | event(s) → read model → UI | An `IChangeHandler` projection — **or**, in Papuma, often just a direct `LoadAsync` (see below). | `GET /orders/{id}` (direct), `GET /orders/{id}/history` (audit), `OrderUiNotifier` (push) |
| **Automation** | read model → processor → command | A handler reacts to a change/event and issues the next write. | `OrderWorkflowHandler` (order → approval task → transition), `ApprovalEscalationService` (the timer) |
| **Translation** | external system → event | An external trigger calls the app API, or `AppendAsync` into the event log; outbound via a bridge handler. | `StockReplenished` (event log); the [NATS bridge](nats-bridge.md) outbound |

The **Command** pattern is where document-sourcing is not just adequate but
*better*. A command must know the current state to decide ("is there stock to
reserve?"). In classic event sourcing you rehydrate the aggregate by replaying
its event stream (and add snapshots when that gets slow). In Papuma the state
*is* the document — `LoadAsync`, done. The single most tedious part of ES —
aggregate rehydration — simply isn't there (concepts §20: the snapshot is
inverted). Look at the PlaceOrder slice: it `LoadAsync<Product>`, decrements
stock, writes the order — no rehydration, no fold-over-events.

## What happens to the Decider?

The functional Decider (Chassaing) is two pure functions, not one:
`decide(command, state) → events` (the decision) and `evolve(state, event) →
state` (the fold that rebuilds state from events). Document-sourcing changes only
the second:

- **`evolve` collapses.** In event sourcing you fold the stream to get the state
  before deciding; in Papuma the state *is* the document (`LoadAsync`), so there
  is nothing to fold. You do not write `evolve` — the database does (diff +
  version on write).
- **`decide` stays — and you should keep it pure.** The business rule ("may this
  order be placed? if so, what is the result?") is the heart of every command
  slice regardless of storage. Extract it as an I/O-free function
  `(Order current, Command cmd) → Order` (or a typed rejection). The handler then
  shrinks to `Load → decide(pure) → Save`.

So the Decider does not vanish; it collapses from two functions to essentially
one. Instead of `decide → events` then `evolve(state, events) → state'`, you
write `decide(command, state) → state'` directly — the decision produces the new
document state rather than events to be folded. That collapse is exactly why the
command slice is simpler here: not because the decision logic disappears, but
because its second half becomes the database's job.

## Testing without infrastructure

One of Event Modeling's best properties is that business logic is testable with
no infrastructure. Papuma delivers that **on the level that matters**, with one
honest caveat:

- **The pure `decide` function: fully infrastructure-free.** If you extracted it
  as above, you test it exactly like an event-sourced decider — GIVEN state, WHEN
  `decide(command)`, THEN new state / expected rejection — in memory, no mock, no
  database, milliseconds. This is the bulk of your business logic and the bulk of
  your tests.
- **The slice as a whole (Load → decide → Save): an integration test** against
  real PostgreSQL (Testcontainers, the way the kernel tests itself). It verifies
  the wiring and the derived change, not the business rule.

Why this is not a step down from event sourcing: in ES you write `evolve`
yourself and test it; in Papuma `evolve` is the database's job, already tested by
the kernel — there is nothing of yours to unit-test there. The "missing"
in-memory test is one you no longer need, not one you lost: less of your own
mechanism means less to test, and the pure logic stays just as isolated.

It only gets hard if you *don't* separate the logic — Load + decision + Save
mashed into one endpoint method forces every logic test through the database.
That is self-inflicted, not a Papuma constraint, and the cure is the Decider
discipline above: keep the decision pure, keep the handler thin. Papuma does not
push you there, but it rewards it — a thin handler is a pure `decide` plus a
couple of slice integration tests, nothing more.

## The slice as a unit of code

Vertical slices are orthogonal to storage — Papuma enforces no layering, so a
slice is just a self-contained folder per blueprint box:

```
Features/
  PlaceOrder/            ← Command slice
    PlaceOrder.cs            command DTO
    PlaceOrderHandler.cs     load → decide → Save/Append
    PlaceOrderTests.cs       GIVEN state, WHEN command, THEN state + change
  OrderSummary/          ← View slice
    OrderSummaryProjection.cs  IChangeHandler → read model (or a direct load)
    OrderSummaryTests.cs       GIVEN changes, THEN read model
  AutoEscalate/          ← Automation slice
    AutoEscalateService.cs     reacts → issues a command
    AutoEscalateTests.cs       GIVEN change/time, THEN command
```

One slice = one box on the wall. The diagram becomes the folder tree; a new
feature is a new folder, not a change spread across controller/service/repo
layers. This is Dilger's point, and Papuma does nothing to obstruct it.

## GIVEN / WHEN / THEN — translated honestly

Event Modeling specifies command slices as **GIVEN events → WHEN command → THEN
events**, and view slices as **GIVEN events → THEN read model**. On Papuma the
shape survives with one substitution: the "GIVEN events" become the **document
state** they would have produced.

```csharp
// Command slice test — GIVEN state, WHEN command, THEN new state + derived change
[Fact]
public async Task PlaceOrder_WithStock_ReservesAndApproves()
{
    // GIVEN: a product with stock 5 (the state prior events would have built)
    await Setup(new Product(sku, "Grinder", 200m), new Inventory(sku, 5));

    // WHEN: the command
    await PlaceOrder(sku, quantity: 1);

    // THEN: the new state and the derived change
    var inv = await session.LoadAsync<Inventory>(sku);
    Assert.Equal(4, inv!.Document.Stock);
    var history = await session.GetHistoryAsync<Inventory>(sku);
    Assert.Equal("Update", history[^1].Operation.ToString());
}
```

If you prefer the literal "GIVEN events" form, you can: the change feed *is* the
event history, so a test helper can assert against `GetHistoryAsync` (the THEN
events) and, if you want to set up state from events, replay them through your
own command handlers rather than inserting documents. But for most slices, "GIVEN
state" is the simpler and equally faithful expression — and it falls straight out
of `SaveAsync`.

## The document-sourced twist on the View pattern

In strict Event Modeling every read model is a projection materialized from
events. Papuma keeps that option (`IChangeHandler` → your own table, ADR-009) but
adds a shortcut the blueprint doesn't assume: because the document *is* the
current state, the most common "state view" is a **direct `LoadAsync`** — no
projection to build, no lag, strongly consistent (concepts §16). Reserve
materialized projections for the cases that actually need them: heavy
aggregations, search indexes, external targets (the decision matrix in §16).

So when you draw a green read-model box, ask: *is this just the current state of
one document?* Then it's a load, not a projection. *Is it an aggregation across
many, or a different store?* Then it's an `IChangeHandler` projection. Both are
"View" slices; Papuma lets the cheap case stay cheap.

## The boundary — when to reach for real Event Sourcing instead

The one place document-sourcing diverges from the strict model is the meaning of
"event." In Event Modeling the **domain event** is the central, intention-bearing
fact. In Papuma the primary derived stream is the technical change feed
(`DocumentChanged` + diff, ADR-011); domain events arise either by translation in
a handler (ADR-011) or explicitly in the event log (`AppendAsync`, ADR-013). For
the vast majority of business applications that is exactly right — and cheaper.

But if your domain requires the **event itself to be the immutable source of
truth** — a financial ledger, a regulatory audit where state must *always* be a
projection of an append-only event stream and nothing else — then you are in the
heart of real event sourcing, and a dedicated ES store (Marten, EventStoreDB) is
more faithful to the model. Papuma deliberately is not that (ADR-002/013: the
event log is never a replay source for state). Modeling with Event Modeling still
applies; only the storage choice flips.

Rule of thumb: **Event Modeling as a method — always. Document-sourced slices —
the simpler, better path for almost all business apps. Event-as-truth domains —
real ES.**

## Where to look in the sample

The sample shop is already an event model; read it through this lens:
- **PlaceOrder** (`POST /orders`) and **DecideApproval** (`POST /approvals/{id}/decide`)
  are command slices — load, decide, write.
- **OrderWorkflowHandler** is the automation slice — the order insert creates the
  approval task; the human's decision drives the transition and the stock
  compensation ([workflow-saga recipe](workflow-saga.md), concepts §18).
- **ApprovalEscalationService** is the timer-driven automation slice.
- **OrderUiNotifier** is a view-update slice (push), and the `GET` endpoints are
  direct state/audit views.

Draw those six boxes on a wall, connect them along a time axis, and you have the
event model the sample implements — built on documents, not an event store.

## Scaffolding and generators

Because slices are structurally uniform (only the Decider varies), the plumbing
is a strong fit for scaffolding — `dotnet new` templates, a source generator, or
an AI agent. The canonical shape of each slice type, with the invariant vs.
variable parts marked, is specified in
[papuma-kernel-slice-conventions.md](../ai/papuma-kernel-slice-conventions.md)
— precise enough to drive a generator. A minimal, runnable example of all three
slice types (one each, with the pure Decider tests) lives in
[samples/event-modeled-slices](../../samples/event-modeled-slices/README.md). The generator itself is an external tool
that consumes that contract; it does not belong in the kernel (which stays
AI-free), and it obtains its context by depending on the package (the docs ship
inside it) and reading the conventions, not by being hand-fed.
