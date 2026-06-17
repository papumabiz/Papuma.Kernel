# Recipe: Causation tracking — who did what and why

Background: [ADR-017](../adr/adr-017-actor-id-column.md) (actor identity),
[ADR-018](../adr/adr-018-causation-type-metadata.md) (causation type),
[concepts §15](../concepts.md) (observability).

## The problem

Every change record and event record answers *what* changed (the diff) and
*when* (the timestamp). But for debugging, audit, and process analysis you also
need:

- **Who** caused the change? → `actor_id` column + `metadata.actorId`
- **What action** triggered it? → `metadata.causationType`
- **Which concrete instance** of that action? → `metadata.causationId`
- **In which request/trace?** → `metadata.correlationId` + `metadata.traceparent`

All of these flow through `SessionOptions` — the application sets them when
opening a session, and the kernel writes them into every change and event record.

## ASP.NET Core: automatic enrichment

The `Papuma.Kernel.AspNetCore` package provides an enricher pattern that
extracts session metadata from the HTTP context automatically.

### 1. Use the built-in enricher

The `ClaimsSessionOptionsEnricher` extracts:

| Field | Source |
|-------|--------|
| `ActorId` | `sub` or `NameIdentifier` claim |
| `CausationType` | Endpoint display name (Minimal API route name or MVC action) |
| `CausationId` | `HttpContext.TraceIdentifier` |

```csharp
// Program.cs — registration
builder.Services.AddPapumaSessionOptions<ClaimsSessionOptionsEnricher>();
```

```csharp
// In an endpoint — usage
app.MapPost("/orders", async (PlaceOrderRequest request, HttpContext http, DocumentStore store) =>
{
    // GetSessionOptions() calls the registered enricher automatically.
    await using var session = store.OpenSession(
        http.GetScopeContext(),
        http.GetSessionOptions());

    // ... writes carry actor, causation type, and causation id automatically.
    await session.SaveAsync(order, 0);
    await session.CommitAsync();
});
```

### 2. Custom enricher

When the default claim mapping does not fit (e.g. custom claim types, API keys,
or explicit command names), implement `ISessionOptionsEnricher` directly:

```csharp
public sealed class MySessionOptionsEnricher : ISessionOptionsEnricher
{
    public SessionOptions Enrich(HttpContext context)
    {
        var actorId = context.User.FindFirstValue("custom_user_id")
                   ?? "anonymous";

        // Use the route pattern as causation type for a stable name.
        var endpoint = context.GetEndpoint();
        var routePattern = (endpoint as RouteEndpoint)?.RoutePattern.RawText;

        return new SessionOptions
        {
            ActorId = actorId,
            CausationType = routePattern ?? endpoint?.DisplayName,
            CausationId = context.TraceIdentifier,
        };
    }
}
```

Register it the same way:

```csharp
builder.Services.AddPapumaSessionOptions<MySessionOptionsEnricher>();
```

## CLI applications: manual setup

CLI applications have no HTTP context, so the enricher pattern does not apply.
Instead, set `SessionOptions` explicitly at each command entry point:

```csharp
async Task ImportOrders(DocumentStore store, ScopeContext scope, ILogger logger)
{
    var commandId = Guid.NewGuid().ToString("N");
    logger.LogInformation("Starting {CommandType} {CommandId}", "ImportOrders", commandId);

    await using var session = store.OpenSession(scope, new SessionOptions
    {
        ActorId = "system:import-cli",
        CausationType = "ImportOrders",
        CausationId = commandId,
    });

    // ... writes ...
    await session.CommitAsync();
}
```

This is typically 3–5 lines per command entry point. CLI applications have few
entry points, so the manual approach is proportionate.

## Change handlers: causation chaining

When a change handler opens a follow-up session, set `CausationId` to the
triggering change's sequence number. This creates a traceable chain within the
database — no external log lookup needed:

```csharp
public sealed class OrderWorkflowHandler(DocumentStore store) : IChangeHandler
{
    public string Name => "order-workflow";

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        await using var session = store.OpenSession(change.Scope, new SessionOptions
        {
            ActorId = change.ActorId,                    // propagate the original actor
            CausationId = $"change:{change.Seq}",        // link back to the trigger
            CausationType = "OrderWorkflowHandler",       // name the handler
        });

        // ... follow-up writes ...
        await session.CommitAsync(ct);
    }
}
```

The resulting metadata chain:

```
Change seq=42  metadata: { causationType: "PlaceOrder", causationId: "req-abc", actorId: "user:7" }
  ↓ handler
Change seq=43  metadata: { causationType: "OrderWorkflowHandler", causationId: "change:42", actorId: "user:7" }
```

## Querying causation in the feed

The `causationType` lives in the JSONB `metadata` column. To query it:

```sql
-- All changes triggered by PlaceOrder
SELECT * FROM papuma.change
WHERE metadata->>'causationType' = 'PlaceOrder'
ORDER BY seq DESC
LIMIT 50;

-- Aggregate: which commands produce the most changes?
SELECT metadata->>'causationType' AS command, COUNT(*) AS changes
FROM papuma.change
WHERE metadata->>'causationType' IS NOT NULL
GROUP BY 1
ORDER BY 2 DESC;
```

If this becomes a hot query path, add a GIN index:

```sql
CREATE INDEX IF NOT EXISTS ix_change_metadata_gin
ON papuma.change USING gin (metadata jsonb_path_ops);
```

## What the metadata object looks like

After all enrichment, a typical change record's metadata contains:

```json
{
  "correlationId": "a1b2c3d4e5f6...",
  "actorId": "user:42",
  "causationId": "req-abc-123",
  "causationType": "PlaceOrder",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

The `correlationId` is auto-generated per session. The `traceparent` is captured
from the active W3C trace context when present (OpenTelemetry integration). All
other fields come from `SessionOptions`.
