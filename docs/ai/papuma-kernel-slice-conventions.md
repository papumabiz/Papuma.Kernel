# Papuma.Kernel — Slice Conventions (for humans, agents and generators)

Status: convention spec (2026-06-13). Companion to
[event-modeling-slices.md](../vNEXT/recipes/event-modeling-slices.md) (the why)
and the [playbook](papuma-kernel-playbook.md) (the rules). This document is the
**canonical shape of each slice type** — precise enough to drive a code generator
or guide an AI agent, and explicit about what is *invariant* (the generator
emits it) versus *variable* (a human/agent fills it).

Why this exists: Event Modeling slices are structurally uniform — the plumbing
repeats per slice type, only the business decision varies. That uniformity is
what makes scaffolding (template- or agent-based) viable. A generator is an
**external tool that consumes this contract**; it does not belong in the kernel
(which stays AI-free). The generator gets its context by depending on the
`Papuma.Kernel` package (the docs ship inside it under `docs/`), copying the
[AGENTS.md snippet](papuma-kernel-agents-snippet.md), and reading this file.

## Slice anatomy

```
Features/<SliceName>/
  <SliceName>.cs          # the command/query DTO            [INVARIANT shape]
  <SliceName>Decider.cs   # pure business logic              [VARIABLE — the value]
  <SliceName>Handler.cs   # load → decide → write            [INVARIANT shape]
  <SliceName>Endpoint.cs  # IEndpointRouteBuilder mapping     [INVARIANT shape]
  <SliceName>Tests.cs     # GIVEN/WHEN/THEN                   [INVARIANT shape, VARIABLE asserts]
```

One slice = one box on the Event Modeling wall. Invariant parts a generator emits
verbatim from the slice name + document type; variable parts are the decision
rule and the assertions.

## Command slice (state change)

The only genuinely variable file is the **Decider** — a pure function. Everything
else is mechanical.

```csharp
// PlaceOrder.cs — DTO. INVARIANT shape (fields from the model).
public sealed record PlaceOrder(string ProductId, int Quantity, string? CustomerEmail);

// PlaceOrderDecider.cs — VARIABLE: the business rule, pure, no I/O.
public static class PlaceOrderDecider
{
    public static Order Decide(Product product, Inventory stock, PlaceOrder cmd)
    {
        if (stock.Stock < cmd.Quantity) throw new OutOfStockException(product.Id);
        var total = product.Price * cmd.Quantity;
        return new Order(NewId(), product.Id, cmd.Quantity, total,
            total > 500m ? OrderStatus.PendingApproval : OrderStatus.Approved, cmd.CustomerEmail);
    }
}

// PlaceOrderHandler.cs — INVARIANT: load → decide → write, in one session.
public static async Task<Order> Handle(DocumentStore store, ScopeContext scope, PlaceOrder cmd, CancellationToken ct)
{
    await using var session = store.OpenSession(scope, new SessionOptions { ActorId = cmd.CustomerEmail });
    var product = await session.LoadAsync<Product>(cmd.ProductId, ct) ?? throw new DocumentNotFoundException(nameof(Product), cmd.ProductId);
    var stock = await session.LoadAsync<Inventory>(cmd.ProductId, ct)!;
    await session.PatchAsync<Inventory>(cmd.ProductId, p => p.Increment(x => x.Stock, -cmd.Quantity), ct: ct);
    var order = PlaceOrderDecider.Decide(product.Document, stock!.Document, cmd);
    await session.SaveAsync(order, expectedVersion: 0, ct);
    await session.CommitAsync(ct);
    return order;
}
```

Invariant rules a generator enforces (from the playbook/ADRs):
- `SaveAsync` always with `expectedVersion`; `0` for new aggregates.
- One `DocumentSession`, one `CommitAsync`.
- Decision logic lives in the Decider, never in the handler — keeps the handler
  thin and the logic unit-testable without a database.

## View slice (state view)

Decide first: is it the current state of one document, or an aggregation/other
store? (concepts §16)

```csharp
// Direct view — no projection, strongly consistent. INVARIANT.
public static Task<DocumentResult<Order>?> Handle(DocumentStore store, ScopeContext scope, string id, CancellationToken ct)
{
    // (open session, LoadAsync<Order>(id), return)
}

// Projection view — for aggregations/search/external targets. INVARIANT shape,
// VARIABLE mapping body.
public sealed class OrderSummaryProjection(/* target client */) : IChangeHandler
{
    public string Name => "order-summary";   // checkpoint identity — never rename
    public async Task HandleAsync(ChangeRecord change, CancellationToken ct) { /* VARIABLE: upsert */ }
}
```

## Automation slice (processor / reactor)

Reacts to a change/event and issues the next command. INVARIANT shape; the
trigger condition and the issued command are VARIABLE.

```csharp
public sealed class OnOrderPlaced(DocumentStore store) : IChangeHandler
{
    public string Name => "on-order-placed";
    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != nameof(Order) || change.Operation != ChangeOperation.Insert) return; // VARIABLE trigger
        // VARIABLE: build + issue the next command (idempotent — deterministic id, §18)
    }
}
```

Rules: handlers are idempotent (at-least-once), never block on humans (materialize
a task document instead, §18), and `Name` is the checkpoint identity.

## Translation slice (integration edge)

External input → a write or an `AppendAsync` fact; outbound → a bridge handler
publishing to a bus (the [NATS bridge](../vNEXT/recipes/nats-bridge.md)).
Inbound is a command slice whose DTO comes from the external contract; outbound
is an automation slice whose action is a publish.

## The GIVEN / WHEN / THEN test (per slice)

Two layers (event-modeling-slices.md, "Testing without infrastructure"):

```csharp
// Layer 1 — the Decider, pure, no infrastructure. Many of these, fast.
[Fact]
public void PlaceOrder_OverStock_Rejected() =>
    Assert.Throws<OutOfStockException>(() =>
        PlaceOrderDecider.Decide(product, new Inventory(sku, 0), new PlaceOrder(sku, 1, null)));

// Layer 2 — the slice end-to-end against PostgreSQL (Testcontainers). A few.
[Fact]
public async Task PlaceOrder_WithStock_ReservesAndApproves() { /* GIVEN docs, WHEN Handle, THEN state + change */ }
```

## What a generator emits vs. what a human/agent fills

| File | Generated from | Filled by |
|---|---|---|
| DTO | slice name + selected model fields | — |
| Handler | slice type template + document type | — |
| Endpoint | slice name + route convention | — |
| Test scaffold | slice type template | the assertions |
| **Decider** | a stub signature | **the human/agent — the business rule** |

The generator's job ends exactly where judgement begins. It removes the
repetitive 80%; the Decider is the 20% that is the actual product. An agent may
*propose* a Decider body from the event model, but that proposal is reviewed like
any business logic — it is not mechanical.

## How a generator obtains the context

1. **Depend on `Papuma.Kernel`** — the package ships `docs/` (playbook, this
   file, concepts, ADRs, wire format). After `dotnet restore` they are on disk.
2. **Copy the [AGENTS.md snippet](papuma-kernel-agents-snippet.md)** into the
   generator project's `AGENTS.md`/`CLAUDE.md` so the rules are always in context.
3. **Read this file** as the slice spec and the [sample](../../samples/Papuma.Kernel.Sample/README.md)
   as the few-shot example.
4. Optionally point an agent at the running app's **MCP** `get_model_inventory`
   for the live type/policy/key list.

The context is a versioned, machine-readable contract — the generator references
it, it is not hand-fed. That is the whole point of shipping the docs in the
package (phase 13).
