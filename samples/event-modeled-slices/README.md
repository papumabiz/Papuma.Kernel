# Event-modeled vertical slices on Papuma.Kernel

A deliberately tiny sample showing the **shape** of event-modeled slices
(Dymitruk/Dilger) on a document-sourced kernel — one slice of each type, nothing
more. For the breadth of kernel features see the
[full Minimal-API sample](../shop-minimal-api/README.md); for the *why* read
[event-modeling-slices.md](../../docs/recipes/event-modeling-slices.md) and
the [slice conventions](../../docs/ai/papuma-kernel-slice-conventions.md).

> This sample is opt-in architecture. The kernel does not require slices or Event
> Modeling — the Minimal-API sample is the neutral "here are the features" view.
> This one is for teams that want to build *this* way (and as a few-shot reference
> for scaffolding/generators).

## The three slice types, one each

```
Features/
  PlaceOrder/        ← Command slice (state change)
    PlaceOrder.cs          command DTO
    PlaceOrderDecider.cs   the business rule — a PURE function (the only variable part)
    PlaceOrderHandler.cs   load → decide → write (one session, one commit)
    PlaceOrderEndpoint.cs  POST /orders
  OrderById/         ← View slice (state view)
    OrderByIdEndpoint.cs   GET /orders/{id} — a direct LoadAsync, not a projection
  OnOrderPlaced/     ← Automation slice (processor)
    OnOrderPlaced.cs       reacts to the order's insert, appends an OrderPlaced fact
```

One slice = one box on the Event Modeling wall. `Program.cs` is just bootstrap +
`app.MapPlaceOrder()` / `app.MapOrderById()` — each slice owns its wiring.

## What each slice demonstrates

- **Command — `PlaceOrder`.** The Decider is a pure `(Product, Inventory,
  PlaceOrder) → Order` with no I/O: it checks stock and decides Approved vs.
  PendingApproval by total. The handler stays thin (load → decide → patch stock →
  save order). The `evolve` half of a classic decider collapses — the decision
  produces the new state directly (concepts §20).
- **View — `OrderById`.** The document-sourced twist (concepts §16): the current
  state of one document is a direct `LoadAsync`, strongly consistent, no
  projection to maintain.
- **Automation — `OnOrderPlaced`.** An `IChangeHandler` that reacts to the order's
  insert and appends an `OrderPlaced` domain fact to the event log. Idempotent,
  non-blocking, `Name` is the checkpoint identity.

## Testing without infrastructure

The whole point of Event Modeling's testability survives here. The Decider tests
([tests/PlaceOrderDeciderTests.cs](tests/PlaceOrderDeciderTests.cs)) run with
**no database, no mocks** — pure GIVEN/WHEN/THEN in milliseconds:

```bash
dotnet test samples/event-modeled-slices/tests   # 4 tests, ~30 ms, no container
```

The slice end-to-end (load → decide → save → derived change) is an integration
test against PostgreSQL, the way the kernel tests itself — that layer verifies
the wiring, not the business rule.

## Run it

```bash
docker run -d --name pg -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=papuma_slices \
  -p 5432:5432 postgres:18-alpine
dotnet run --project samples/event-modeled-slices

# A small order auto-approves; a large one (> 500) needs approval; oversell → 409.
curl -s -X POST localhost:5000/orders -H 'content-type: application/json' \
  -d '{"productId":"demo-grinder","quantity":1}'
curl -s localhost:5000/orders/<id>
```

A `demo-grinder` product (stock 10) is seeded at startup so `/orders` works out
of the box. (Use `127.0.0.1` over `localhost` if your Postgres is in a container
published on IPv4 only.)

Verified end-to-end 2026-06-13: small order → Approved, large → PendingApproval,
oversell → 409, stock 10→6, two `OrderPlaced` events appended by the automation.
