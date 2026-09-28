# Papuma.Kernel — Slice Conventions (for humans, agents and generators)

Status: convention spec (2026-06-13). Companion to
[event-modeling-slices.md](../recipes/event-modeling-slices.md) (the why)
and the [playbook](papuma-kernel-playbook.md) (the rules). This document is the
**canonical shape of each slice type** — precise enough to drive a code generator
or guide an AI agent, and explicit about what is *invariant* (the generator
emits it) versus *variable* (a human/agent fills it).

Why this exists: Event Modeling slices are structurally uniform — the plumbing
repeats per slice type, only the business decision varies. That uniformity is
what makes scaffolding (template- or agent-based) viable. A generator is an
**external tool that consumes this contract**; it does not belong in the kernel
(which stays AI-free). The generator gets its context by depending on the
`Papuma.Kernel` or `Papuma.Kernel.Local` package (the docs ship inside either
under `docs/`), copying the relevant block of the
[AGENTS.md snippet](papuma-kernel-agents-snippet.md), and reading this file —
the slice shape below is storage-neutral, the same on both kernels.

## Slice anatomy

```
Features/<SliceName>/
  <SliceName>.cs          # the command/query DTO            [INVARIANT shape]
  <SliceName>Decider.cs   # pure business logic              [VARIABLE — the value]
  <SliceName>Handler.cs   # load → decide → write            [INVARIANT shape]
  <SliceName>Endpoint.cs  # IEndpointRouteBuilder mapping     [INVARIANT shape]
  <SliceName>Registration.cs # model fragment + handler wiring [INVARIANT shape]
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
public sealed class OrderSummaryProjection(/* target client */) : IChangeHandler, IProjection
{
    public string Name => "order-summary";   // checkpoint identity — never rename
    public int Version => 1;                 // INVARIANT: raise to rebuild (ADR-024)
    public async Task ResetAsync(CancellationToken ct) { /* VARIABLE: empty the target, e.g. TRUNCATE */ }
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
a task document instead, §18), and `Name` is the checkpoint identity. An automation is
not a projection — never reset it. Added to a system with history, it starts at the
beginning of the feed and reacts to every past record; put `[StartsAtFeedHead]` on it
when it should react to new records only (ADR-024).

## Translation slice (integration edge)

External input → a write or an `AppendAsync` fact; outbound → a bridge handler
publishing to a bus (the [NATS bridge](../recipes/nats-bridge.md)).
Inbound is a command slice whose DTO comes from the external contract; outbound
is an automation slice whose action is a publish.

## Registration: each slice wires itself

The slice owns its endpoint (`MapPlaceOrder()`) — and, by the same logic, the
documents/events it introduces and the handlers it registers. Both kernel
builders are fluent (`KernelModelBuilder.Document<T>()` and
`PapumaKernelBuilder.AddChangeHandler<T>()` return the builder), so a slice
contributes via two small extension methods next to its code:

```csharp
// Features/PlaceOrder/PlaceOrderRegistration.cs — co-located with the slice.
public static class PlaceOrderRegistration
{
    // the model fragment this slice owns (a generator emits this from the slice's types)
    public static KernelModelBuilder AddOrdering(this KernelModelBuilder m) => m
        .Document<Order>()
        .Document<Inventory>(d => d.Validate(inv =>
        {
            if (inv.Stock < 0) throw new OutOfStockException(inv.Id);
        }));

    // the handlers this slice owns
    public static PapumaKernelBuilder AddOrderingHandlers(this PapumaKernelBuilder k) => k
        .AddChangeHandler<OnOrderPlaced>();
}
```

`Program.cs` then reads as a table of contents — one line per slice, no growing
blob:

```csharp
builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = config.GetConnectionString("papuma");
        o.Model(m => m.AddOrdering().AddCatalog().AddUsers());   // each slice's fragment
    })
    .AddOrderingHandlers()                                       // each slice's handlers
    .AddCatalogHandlers();

app.MapPlaceOrder();   // each slice's endpoint (already the convention above)
app.MapOrderById();
```

Why this and not one central `AddMyApp()` extension: a monolith that relocates
the same lines hides nothing — it only adds indirection. Co-locating keeps every
definition with the feature that owns it, so adding or deleting a slice touches
one folder, and the bootstrap file stays a readable index. (For a small, single-
slice or non-slice app, inline registration is fine — extract only when the model
block actually grows. Don't add the seam before there is bloat to absorb.)

## The GIVEN / WHEN / THEN test (per slice)

Two layers (event-modeling-slices.md, "Testing without infrastructure"):

```csharp
// Layer 1 — the Decider, pure, no infrastructure. Many of these, fast.
[Fact]
public void PlaceOrder_OverStock_Rejected() =>
    Assert.Throws<OutOfStockException>(() =>
        PlaceOrderDecider.Decide(product, new Inventory(sku, 0), new PlaceOrder(sku, 1, null)));

// Layer 2 — the slice end-to-end against PostgreSQL (Papuma.Kernel.Testing:
// PapumaTestDatabase + DrainAsync) or a temp-file SQLite DB for
// Papuma.Kernel.Local apps. A few.
[Fact]
public async Task PlaceOrder_WithStock_ReservesAndApproves() { /* GIVEN docs, WHEN Handle, THEN state + change */ }
```

## What a generator emits vs. what a human/agent fills

| File | Generated from | Filled by |
|---|---|---|
| DTO | slice name + selected model fields | — |
| Handler | slice type template + document type | — |
| Endpoint | slice name + route convention | — |
| Registration | slice name + the documents/handlers it owns | — |
| Test scaffold | slice type template | the assertions |
| **Decider** | a stub signature | **the human/agent — the business rule** |

The generator's job ends exactly where judgement begins. It removes the
repetitive 80%; the Decider is the 20% that is the actual product. An agent may
*propose* a Decider body from the event model, but that proposal is reviewed like
any business logic — it is not mechanical.

## Using Papuma.Kernel.FSharp (F#)

The slice *shape* above is language-neutral — DTO/Decider/Handler/Endpoint/
Registration/Test stays the same anatomy across both languages. Only the
concrete syntax changes: lambdas to `obj` need an explicit `box`, and expected
write outcomes can come back as `Result` instead of exceptions.
See [Papuma.Kernel.FSharp](https://github.com/papumabiz/Papuma.Kernel/blob/master/src/Papuma.Kernel.FSharp/README.md) and
[concepts.md §29](../concepts.md#29-f-as-a-facade-not-a-rewrite--and-why-the-wire-format-stays-closed)
for the reasoning; this section is the slice-shape translation only.

One genuine improvement, not just a translation: the **Decider fits F# better
than C#**. It was always meant to be a pure function — F# just doesn't make
you wrap it in a static class to say so.

### Command slice, translated

```fsharp
// PlaceOrder.fs — DTO. INVARIANT shape, same fields as the C# version. Plain
// `string`, not `string option` — a command DTO is deserialized the same way
// a document is (System.Text.Json), so the same option/DU limitation applies
// (concepts §29).
type PlaceOrder = { ProductId: string; Quantity: int; CustomerEmail: string }

// PlaceOrderDecider — VARIABLE: the business rule, pure, no I/O. Takes the
// pre-loaded stock (same as the C# version) for the early, synchronous
// rejection; the actual oversell guarantee is the atomic Increment below plus
// a `Validate` on Inventory (concepts §17) — the Decider's check is a UX
// nicety on top, not the correctness mechanism.
module PlaceOrderDecider =
    let decide (product: Product) (stock: Inventory) (cmd: PlaceOrder) : Order =
        if stock.Stock < cmd.Quantity then raise (OutOfStockException(product.Id))
        let total = product.Price * decimal cmd.Quantity
        { Id = Guid.NewGuid().ToString("N")
          ProductId = product.Id
          Quantity = cmd.Quantity
          Total = total
          Status = if total > 500m then "pending-approval" else "approved"
          CustomerEmail = cmd.CustomerEmail }

// PlaceOrderHandler — INVARIANT: load → decide → write, one session, one
// commit. runSessionCommitted commits when the body returns Ok and discards
// the writes on Error — the forgotten CommitAsync cannot happen.
// Patch takes F# lambdas (`box` where the parameter is obj), and trySaveAsync/
// tryPatchAsync return Result instead of throwing — DocumentNotFoundException
// becomes a match arm, not a try/catch.
let handle (store: SqliteDocumentStore) (scope: ScopeContext) (cmd: PlaceOrder) : Task<Result<Order, KernelError>> =
    runSessionCommitted (store.OpenSession(scope)) (fun session ->
        task {
            let! product = session.LoadAsync<Product>(cmd.ProductId)
            let! stock = session.LoadAsync<Inventory>(cmd.ProductId)

            match product, stock with
            | null, _
            | _, null -> return Error(DocumentNotFound(nameof Product, cmd.ProductId))
            | product, stock ->
                let! stockOutcome =
                    tryPatchAsync
                        session
                        cmd.ProductId
                        (fun p -> p.Increment((fun (x: Inventory) -> box x.Stock), int64 -cmd.Quantity) |> ignore)
                        None

                match stockOutcome with
                | Error err -> return Error err
                | Ok _ ->
                    let order = PlaceOrderDecider.decide product.Document stock.Document cmd
                    let! saveOutcome = trySaveAsync session order 0L
                    return saveOutcome |> Result.map (fun _ -> order)
        })
```

`trySaveAsync`/`tryPatchAsync`/`runSession`/`runSessionCommitted` are written against statically
resolved type parameters (SRTP), so the same handler code compiles unchanged
against `Papuma.Kernel`'s `DocumentStore`/`DocumentSession` (Postgres) — swap
the type annotation, nothing else.

### Endpoint slice — the delegate-conversion gotcha

Minimal API endpoint mapping needs an explicit `Func<_,_>` wrapper: F# doesn't
implicitly convert a function value into a delegate the way C# does at a
directly-typed call site (see `samples/fsharp-local-todo/Program.fs`):

```fsharp
app.MapPost("/orders", Func<HttpContext, Task>(placeOrderEndpoint)) |> ignore
```

### Registration — module functions instead of extension methods

C#'s fluent extension-method chaining (`this KernelModelBuilder m`) has no F#
equivalent worth reaching for; a plain function taking and returning the
builder reads just as well, no ceremony needed:

```fsharp
module OrderingRegistration =
    let addOrdering (m: KernelModelBuilder) : KernelModelBuilder =
        m.Document<Order>().Document<Product>()

    let addOrderingHandlers (k: PapumaKernelLocalBuilder) : PapumaKernelLocalBuilder =
        k.AddChangeHandler<OnOrderPlaced>()
```

### View, Automation, Translation slices

Same translation pattern as the Command slice — `LoadAsync` is unchanged,
`IChangeHandler` is a plain interface F# implements natively (see
`TodoChangeLogger` in the sample), and a slice's *Patch* calls take F# lambdas
like everywhere else. No slice type needs a second write-up here.

### Tests — backtick names instead of PascalCase methods

```fsharp
[<Fact>]
let ``PlaceOrder over stock is rejected`` () =
    let outOfStock = { Id = "sku-1"; Stock = 0 }
    Assert.Throws<OutOfStockException>(fun () -> PlaceOrderDecider.decide product outOfStock cmd |> ignore)
```

## How a generator obtains the context

1. **Depend on `Papuma.Kernel`** — the package ships `docs/` (playbook, this
   file, concepts, ADRs, wire format). After `dotnet restore` they are on disk.
2. **Copy the [AGENTS.md snippet](papuma-kernel-agents-snippet.md)** into the
   generator project's `AGENTS.md`/`CLAUDE.md` so the rules are always in context.
3. **Read this file** as the slice spec and the [sample](https://github.com/papumabiz/Papuma.Kernel/blob/master/samples/shop-minimal-api/README.md)
   as the few-shot example.
4. Optionally point an agent at the running app's **MCP** `get_model_inventory`
   for the live type/policy/key list.

The context is a versioned, machine-readable contract — the generator references
it, it is not hand-fed. That is the whole point of shipping the docs in the
package (phase 13).
