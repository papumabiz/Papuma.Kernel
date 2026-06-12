// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

// The Papuma.Kernel sample shop — every kernel concept as running code.
// Walkthrough in README.md; the workflow story in docs/vNEXT/recipes/workflow-saga.md.

using Papuma.Kernel.AspNetCore.Processing;
using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Mcp;
using Papuma.Kernel.Sample;
using Papuma.Kernel.Sample.Handlers;
using Papuma.Kernel.Sample.Services;
using Papuma.Kernel.Store;

// ASP.NET Core has its own SessionOptions — the kernel's is meant everywhere here.
using SessionOptions = Papuma.Kernel.Store.SessionOptions;

var builder = WebApplication.CreateBuilder(args);

// ── Kernel bootstrap: model, schema, feed workers (getting-started §2) ─────────
builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("papuma")
            ?? "Host=localhost;Port=5432;Database=papuma_sample;Username=postgres;Password=postgres";
        o.Model(m => m
            .Document<Product>()
            .Document<Inventory>(d => d.Validate(inv =>
            {
                // The bounded counter (concepts §17): Increment + this validator
                // form the atomic conditional decrement — overselling is impossible.
                if (inv.Stock < 0)
                {
                    throw new OutOfStockException(inv.Id);
                }
            }))
            .Document<Order>()
            .Document<ApprovalTask>()
            .Event<StockReplenished>(e => e.Retention(TimeSpan.FromDays(365))));
    })
    .AddChangeHandler<OrderWorkflowHandler>()   // the saga (concepts §18)
    .AddChangeHandler<OrderUiNotifier>();       // the realtime push recipe

builder.Services.AddPapumaScope<DemoScopeResolver>();
builder.Services.AddHostedService<ApprovalEscalationService>(); // the timer primitive
builder.Services.AddSignalR();
builder.Services.AddHealthChecks().AddPapumaChangeFeedLag(maxAllowedLag: 1000);

// ── MCP server: the diagnostics surface for AI agents (observability.md) ───────
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithPapumaKernel(); // read-only default; mutations need AllowMutations opt-in

var app = builder.Build();
app.UseScopeResolution();

// ── Catalog ─────────────────────────────────────────────────────────────────────

// Create a product with initial stock: two documents + one fact, ONE atomic commit
// sharing a correlationId ("session = unit of work", architecture §5).
app.MapPost("/products", async (CreateProduct request, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var id = Guid.NewGuid().ToString("N");
    await session.SaveAsync(new Product(id, request.Name, request.Price), expectedVersion: 0);
    await session.SaveAsync(new Inventory(id, request.InitialStock), expectedVersion: 0);
    await session.AppendAsync(new StockReplenished(id, request.InitialStock));
    await session.CommitAsync();
    return Results.Created($"/products/{id}", new { id });
});

// Replenish stock: Increment needs no expectedVersion (atomic in the statement,
// ADR-012) — and the fact of the delivery goes to the event log (ADR-013).
app.MapPost("/products/{id}/replenish", async (string id, Replenish request, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    await session.PatchAsync<Inventory>(id, p => p.Increment(x => x.Stock, request.Quantity));
    await session.AppendAsync(new StockReplenished(id, request.Quantity));
    await session.CommitAsync();
    return Results.NoContent();
});

// ── Ordering: bounded counter + workflow entry ──────────────────────────────────

app.MapPost("/orders", async (PlaceOrder request, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext(),
        new SessionOptions { ActorId = request.CustomerEmail });

    var product = await session.LoadAsync<Product>(request.ProductId);
    if (product is null)
    {
        return Results.NotFound(new { error = $"Unknown product {request.ProductId}." });
    }

    // 1. Atomic conditional decrement (concepts §17): all buyers up to stock 0
    //    succeed without retries; the next one gets the typed rejection below.
    try
    {
        await session.PatchAsync<Inventory>(request.ProductId,
            p => p.Increment(x => x.Stock, -request.Quantity));
    }
    catch (OutOfStockException)
    {
        return Results.Conflict(new { error = "Out of stock." });
    }

    // 2. The workflow instance: expensive orders enter the human-in-the-loop path.
    //    Stock decrement + order commit atomically — no order without stock,
    //    no reserved stock without an order.
    var total = product.Document.Price * request.Quantity;
    var order = new Order(
        Id: Guid.NewGuid().ToString("N"),
        ProductId: request.ProductId,
        Quantity: request.Quantity,
        Total: total,
        Status: total > 500m ? OrderStatus.PendingApproval : OrderStatus.Approved,
        CustomerEmail: request.CustomerEmail);
    await session.SaveAsync(order, expectedVersion: 0);
    await session.CommitAsync();

    return Results.Created($"/orders/{order.Id}", new { order.Id, order.Status, order.Total });
});

// The human decision (concepts §18): a normal patch with expectedVersion — two
// concurrent approvers serialize typed, the loser sees 409 with the history hint.
app.MapPost("/approvals/{taskId}/decide", async (string taskId, Decide request, DocumentStore store, HttpContext http) =>
{
    if (request.Decision is not (ApprovalStatus.Approved or ApprovalStatus.Rejected))
    {
        return Results.BadRequest(new { error = "Decision must be Approved or Rejected." });
    }

    await using var session = store.OpenSession(http.GetScopeContext(),
        new SessionOptions { ActorId = request.DecidedBy });

    var task = await session.LoadAsync<ApprovalTask>(taskId);
    if (task is null)
    {
        return Results.NotFound();
    }

    if (task.Document.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected)
    {
        return Results.Conflict(new { error = $"Already decided: {task.Document.Status}." });
    }

    try
    {
        await session.PatchAsync<ApprovalTask>(taskId, p => p
            .Set(x => x.Status, request.Decision)
            .Set(x => x.DecidedBy, request.DecidedBy),
            expectedVersion: task.Version);
        await session.CommitAsync();
    }
    catch (ConcurrencyException)
    {
        return Results.Conflict(new { error = "Someone else decided or escalated in the meantime — reload." });
    }

    return Results.NoContent();
});

// ── Reads: strong consistency + the audit trail ─────────────────────────────────

app.MapGet("/orders/{id}", async (string id, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var order = await session.LoadAsync<Order>(id);
    return order is null ? Results.NotFound() : Results.Ok(new { order.Document, order.Version });
});

// The order's change feed IS the workflow execution log (concepts §18) —
// policy-applied: customerEmail shows up only as {"changed": true}.
app.MapGet("/orders/{id}/history", async (string id, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var history = await session.GetHistoryAsync<Order>(id);
    return Results.Ok(history.Select(c => new
    {
        c.Version,
        Operation = c.Operation.ToString(),
        c.OccurredAt,
        Actor = (string?)c.Metadata["actorId"],
        Diff = c.Diff.ToJson(),
    }));
});

app.MapGet("/products/{id}/stock", async (string id, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var inventory = await session.LoadAsync<Inventory>(id);
    return inventory is null ? Results.NotFound() : Results.Ok(new { inventory.Document.Stock });
});

app.MapHub<ShopHub>("/hub/shop");   // realtime push (recipe: realtime-ui-notifications)
app.MapHealthChecks("/health");     // includes the change-feed lag check
app.MapMcp("/mcp");                 // AI agents: get_feed_lag, get_document_history, …

app.Run();

// ── Request DTOs ────────────────────────────────────────────────────────────────

public sealed record CreateProduct(string Name, decimal Price, int InitialStock);

public sealed record Replenish(int Quantity);

public sealed record PlaceOrder(string ProductId, int Quantity, string? CustomerEmail = null);

public sealed record Decide(string Decision, string DecidedBy);
