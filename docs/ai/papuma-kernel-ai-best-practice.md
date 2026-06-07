# Papuma.Kernel AI Best Practice Guide

This document teaches AI coding agents **how to think about and build applications** on Papuma.Kernel. It is not an API reference (see [the API reference](papuma-kernel-api-reference.md)) and not an architecture briefing (see [the framework context](papuma-kernel-ai-framework-context.md)). It is the **senior architect's playbook** — when to use what, how to structure code, and which patterns produce maintainable, GDPR-safe, multi-tenant applications.

**Target audience**: AI agents in other repositories that need to implement features, projections, and infrastructure on Papuma.Kernel.

---

## 1. The Mental Model

Papuma.Kernel is a **CRUD-truth + change-feed** architecture. Think of it like this:

```
Your domain tables (orders, users, invoices) are the SOURCE OF TRUTH.
You CAN read them directly with SQL at any time.

The change_feed is a SYNCHRONIZATION BUS.
It tells projections "something changed, here's the fact."
Projections build read models ASYNCHRONOUSLY from the feed.
```

**One sentence to memorize**: *Writes commit facts to domain tables and the change feed in one transaction. Projections converge asynchronously.*

### What this framework IS

- A lightweight infrastructure layer for PostgreSQL-backed applications
- A reliable way to synchronize read models without coupling write latency
- A GDPR-ready event store with explicit redaction
- A multi-tenant foundation with PostgreSQL Row-Level Security

### What this framework IS NOT

- Pure event sourcing (domain tables are still the truth)
- A message broker replacement (it polls PostgreSQL, it doesn't push)
- An ORM (you write raw SQL against Npgsql)
- A magic projection orchestrator (you control what runs where)

---

## 2. The Three Event Tables: When to Use Which

This is the most important decision you'll make. Every state change in your application must be classified into one or more of these three destinations.

| Table | Purpose | Example | Key question |
|-------|---------|---------|-------------|
| `change_feed` | State transition facts for **read-model rebuild** | `OrderCreated`, `UserEmailUpdated`, `InvoiceApproved` | Is this needed to reconstruct the current state of the domain? |
| `business_event_log` | Semantic domain signals that don't change state | `UserLoggedIn`, `CheckoutViewed`, `ReportGenerated` | Is this a meaningful business occurrence that doesn't change a domain table? |
| `event_outbox` | Reliable **external** message delivery | Publish to RabbitMQ, call webhook, notify Slack | Does an external system need to know about this? |

### Decision Flow

```
A domain operation happens.
   │
   ├─ Did a domain table change?
   │    YES → Write to change_feed (ALWAYS)
   │
   ├─ Is there a business-significant event (even without state change)?
   │    YES → Write to business_event_log
   │    │
   │    └─ Does an external system need to be notified?
   │         YES → Also enqueue in event_outbox (same transaction!)
   │
   └─ Only a state change, no external notification needed?
        → change_feed only (projections will pick it up)
```

### Concrete Examples

**Example 1: User changes email**
```
Domain table `users` updated → change_feed: `UserEmailUpdated`
No external system needs this → no outbox needed
```

**Example 2: User logs in**
```
No domain table changed → NO change_feed entry
Login is a business signal → business_event_log: `UserLoggedIn`
Analytics system needs this → outbox: enqueue for analytics pipeline
```

**Example 3: Invoice approved in backoffice**
```
Domain table `invoices.status` changed → change_feed: `InvoiceApproved`
Business signal for process → business_event_log: `InvoiceApproved`  
Email notification needed → outbox: enqueue for notification service
```

### Anti-Pattern: Writing Everything to change_feed

Do NOT write `UserLoggedIn` to `change_feed`. It's not a state transition — no domain table changed. It bloats the feed, slows down replays, and confuses projections that are trying to rebuild read models. Use `business_event_log` for signals without state change.

### Anti-Pattern: Skipping change_feed for State Changes

Do NOT update a domain table without writing to `change_feed`. Projections rely on the feed to know something changed. If you skip it, read models go stale and replays produce wrong results.

### Payload Design: What Goes Into Each Table

The three tables serve different consumers with different needs. **Do not copy the same payload blindly into all three.**

| Table | Consumer | Payload principle |
|-------|----------|-------------------|
| `change_feed` | Projections (read-model rebuild) | **Complete** — everything needed to reconstruct state |
| `business_event_log` | Analytics, audit, process signals | **Selective** — only what the signal consumer needs |
| `event_outbox` | External systems (broker, webhook) | **Contract-driven** — only what the external API contract requires |

**`change_feed` payload**: Always complete. Projections must be able to reconstruct the full read model from the feed alone. If a field is missing, replays produce wrong results.

**`business_event_log` payload**: Lean and purpose-specific. Include only the fields that make the signal meaningful. For high-frequency events (login, page view, cart interaction), a minimal payload keeps the log performant and reduces GDPR surface area.

```
UserLoggedIn  → {"userId":"u-1", "ip":"1.2.3.4", "method":"password"}
               NOT the full user object — no name, email, or address needed here

CheckoutViewed → {"sessionId":"s-1", "cartItemCount":3, "totalAmount":42.00}
                NOT all product details — the signal is the intent, not the cart contents

InvoiceApproved → {"invoiceId":"inv-1", "approvedBy":"user:admin", "amount":1200.00}
                  NOT the full invoice line items — the signal is the approval fact
```

**`event_outbox` payload**: Designed for the external consumer's contract, not for internal use. Different external systems may need different projections of the same event. Build the outbox payload explicitly — do not reuse the `business_event_log` payload unless the contracts happen to match.

```
Internal change_feed payload:  {"orderId":"o-1", "internalCustomerId":"c-42", "warehouseZoneId":"wz-7", "amount":99.00}
Outbox payload for fulfillment: {"orderId":"o-1", "shippingAddress":{...}, "items":[...]}
Outbox payload for analytics:   {"orderId":"o-1", "amount":99.00, "channel":"web"}
```

**Exception**: When the payload is small, non-sensitive, and genuinely useful to all consumers, reusing it across tables is acceptable. But this should be a deliberate decision, not the default.

---

## 3. The Write Path: One Transaction, Three Writers

Every write operation follows this pattern:

```
Open transaction
  → Write domain state (your SQL)
  → ChangeWriter.AppendAsync()    ← change_feed (always for state changes)
  → BusinessEventWriter.AppendAsync() ← business_event_log (if business signal)
  → OutboxWriter.EnqueueAsync()   ← event_outbox (if external notification)
Commit transaction
```

**Critical rule**: All writers MUST use the SAME `NpgsqlTransaction`. Everything commits or nothing commits.

### Template: Complete Write Handler

```csharp
public class CreateOrderHandler
{
    private readonly IUnitOfWork _uow;
    private readonly ChangeWriter _changeWriter;
    private readonly BusinessEventWriter _eventWriter;
    private readonly OutboxWriter _outboxWriter;

    public CreateOrderHandler(
        IUnitOfWork uow,
        ChangeWriter changeWriter,
        BusinessEventWriter eventWriter,
        OutboxWriter outboxWriter)
    {
        _uow = uow;
        _changeWriter = changeWriter;
        _eventWriter = eventWriter;
        _outboxWriter = outboxWriter;
    }

    public async Task HandleAsync(
        ScopeContext scope,
        CreateOrderCommand cmd,
        string actorId,
        CancellationToken ct)
    {
        await _uow.ExecuteAsync(async (conn, tx, ct) =>
        {
            // 1. Write domain state
            await using var domainCmd = conn.CreateCommand();
            domainCmd.Transaction = tx;
            domainCmd.CommandText = """
                INSERT INTO orders (order_id, customer_id, amount, status, created_at)
                VALUES (@id, @customerId, @amount, 'pending', NOW())
                """;
            domainCmd.Parameters.AddWithValue("id", cmd.OrderId);
            domainCmd.Parameters.AddWithValue("customerId", cmd.CustomerId);
            domainCmd.Parameters.AddWithValue("amount", cmd.Amount);
            await domainCmd.ExecuteNonQueryAsync(ct);

            // 2. Change feed (state transition)
            var payload = $$"""{"orderId":"{{cmd.OrderId}}","amount":{{cmd.Amount}}}""";
            await _changeWriter.AppendAsync(
                tx, scope,
                entity: "Order",
                entityId: cmd.OrderId,
                eventType: "OrderCreated",
                version: 1,
                payloadJson: payload,
                actorId: actorId,
                correlationId: cmd.CorrelationId,
                idempotencyKey: cmd.IdempotencyKey,
                ct: ct);

            // 3. Business event (semantic signal)
            var eventId = await _eventWriter.AppendAsync(
                tx, scope,
                eventType: "OrderPlaced",
                actorId: actorId,
                payloadJson: payload,
                entity: "Order",
                entityId: cmd.OrderId,
                correlationId: cmd.CorrelationId,
                idempotencyKey: cmd.IdempotencyKey,
                ct: ct);

            // 4. Outbox (external notification)
            await _outboxWriter.EnqueueAsync(
                tx, scope,
                eventId: eventId,
                eventType: "OrderPlaced",
                payloadJson: payload,
                ct: ct);

        }, ct);
    }
}
```

### Idempotency Keys: When and How

Pass a stable `idempotencyKey` whenever the operation might be retried:

| Scenario | Use idempotencyKey? | Value |
|----------|---------------------|-------|
| HTTP POST from client with retry | **YES** | Client-generated request ID |
| Message consumer (at-least-once delivery) | **YES** | Message ID from broker |
| Scheduled job | **YES** | `$"{jobRunId}:{entityId}"` |
| Internal synchronous call | Optional | — |

The same key works across `change_feed` and `business_event_log` — both tables have unique constraints on `(scope, tenant_id, idempotency_key)`. On collision, the write is silently skipped (no exception).

---

## 4. Project Structure

### The Standard Layout

```
src/
  MyProduct.Domain.<BoundedContext>/
    <EntityPlural>/
      <Entity>.cs              # Domain entity
      <Entity>Id.cs            # Strongly-typed ID
      <Entity>Status.cs        # Status enum
      Events/
        <Entity>Created.cs     # Payload records (one per event type)
        <Entity>Updated.cs
        <Entity>Approved.cs

  MyProduct.Contracts.<BoundedContext>/
    Events/
      <Entity>CreatedIntegrationEvent.cs   # Contracts for external consumers
      <Entity>ApprovedIntegrationEvent.cs

  MyProduct.Application.<AppName>/
    Features/
      <BoundedContext>/
        <Verb><Entity>/
          Command.cs           # Input DTO
          Handler.cs           # Write-path handler (uses IUnitOfWork + writers)
          Validator.cs         # Input validation
    Projections/
      <BoundedContext>/
        <ReadModelName>Projection.cs  # IProjectionHandler implementations

  MyProduct.Infrastructure/
    Persistence/               # Npgsql setup, migrations
    Outbox/                    # IOutboxPublisher implementations
    Messaging/                 # Message broker adapters
    SensitiveData/             # Sensitive data encryption

  MyProduct.Host.<AppName>/    # ASP.NET Core host (thin — DI wiring only)
    Program.cs
    appsettings.json
```

### Rules for Splitting

1. **Features belong to the app that owns the use case.** A WebApp and a Backoffice app have different features — even for the same domain entity.
2. **Projections belong to the consumer that reads them.** A WebApp's "MyOrders" projection and a Backoffice's "ApprovalQueue" projection are separate.
3. **Domain and Contracts are shared.** Domain rules and event contracts are stable and reused across apps.
4. **Infrastructure is shared.** Outbox publishers, message adapters, persistence setup.
5. **Hosts are thin.** They only do DI wiring — no business logic.

### When to Extract Shared Code

Default: **keep it app-specific.** Only extract to shared when:
- The logic is truly identical (not just similar)
- The data model is identical
- Security and visibility rules are identical
- All consumers have similar release cycles

If any answer is "no" — keep it separate. Duplication is cheaper than wrong coupling.

---

## 5. Projection Patterns

### DB Projection (IProjectionHandler)

Use when the read model lives in PostgreSQL. The handler gets the worker's connection and transaction — your writes and the checkpoint update are atomic.

```csharp
public sealed class OrderSummaryProjection : IProjectionHandler
{
    public string Name => "OrderSummary";
    public IReadOnlyCollection<string> EventTypes => new[] { "OrderCreated", "OrderCancelled" };

    public async Task HandleAsync(
        ChangeRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = record.EventType switch
        {
            "OrderCreated" => """
                INSERT INTO order_summaries (order_id, status, created_at)
                VALUES (@id, 'active', @ts)
                ON CONFLICT (order_id) DO NOTHING
                """,
            "OrderCancelled" => """
                UPDATE order_summaries
                SET status = 'cancelled', updated_at = @ts
                WHERE order_id = @id
                """,
            _ => return
        };

        cmd.Parameters.AddWithValue("id", record.EntityId);
        cmd.Parameters.AddWithValue("ts", record.Timestamp);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

**Key rules for DB projections**:
- Always use `ON CONFLICT ... DO UPDATE` or `ON CONFLICT ... DO NOTHING` — the handler WILL be called more than once for the same event (at-least-once semantics).
- Use the provided `connection` and `transaction` — don't open your own.
- Filter by `record.EventType` — you declared which types you consume in `EventTypes`, but still switch inside `HandleAsync`.
- The framework's `xmin`-based visibility filter prevents sequence gaps. You don't need to worry about missing events due to concurrent transactions.

### External Projection (IExternalProjectionHandler)

Use when the target is NOT PostgreSQL — search index, cache, external API, file system.

```csharp
public sealed class SearchIndexProjection : IExternalProjectionHandler
{
    public string Name => "SearchIndex";
    public IReadOnlyCollection<string> EventTypes => new[] { "OrderCreated", "OrderUpdated" };

    public async Task HandleAsync(ChangeRecord record, CancellationToken ct)
    {
        // MUST be idempotent — no transaction to protect you
        await _searchClient.IndexAsync(new
        {
            Id = $"{record.Entity}:{record.EntityId}",
            Type = record.Entity,
            Action = record.EventType,
            Timestamp = record.Timestamp
        }, ct);
    }
}
```

**Key rules for external projections**:
- **Idempotency is mandatory.** There is no transaction protecting you. The handler can and will be called multiple times for the same event.
- Use the event's `SequenceId` or `Entity:EntityId` as a deduplication key.

### Replayable Projections (IReplayableProjection)

Implement `IReplayableProjection` when your read model can be rebuilt from scratch:

```csharp
public sealed class OrderSummaryProjection : IProjectionHandler, IReplayableProjection
{
    public async Task PrepareReplayAsync(CancellationToken ct)
    {
        // Truncate or reset read model before replay
        await _dataSource.ExecuteAsync("TRUNCATE TABLE order_summaries", ct);
    }
    // ... rest of handler
}
```

### DI Registration

```csharp
// In-DB projection, tenant-scoped
builder.Services.AddProjection<OrderSummaryProjection>(
    scopeFilter: ScopeFilter.Tenant("acme"));

// External projection, platform-scoped
builder.Services.AddExternalProjection<SearchIndexProjection>(
    scopeFilter: ScopeFilter.Platform());

// Replay service (for triggering rebuilds)
builder.Services.AddReplayService();
```

### Scope Filter Decision Table

| Projection type | Typical scope | Reason |
|-----------------|--------------|--------|
| WebApp user dashboard | `ScopeFilter.Tenant("...")` | Users see only their tenant's data |
| Backoffice approval queue | `ScopeFilter.Platform()` or `ScopeFilter.Tenant("...")` | Depends on whether backoffice is per-tenant or cross-tenant |
| Search index | `ScopeFilter.Platform()` | Search across all tenants |
| Analytics/Reporting | `ScopeFilter.All()` | Needs all data |
| Retention worker | `ScopeFilter.Platform()` | Platform-level cleanup |

---

## 6. Scope and Multi-Tenancy Patterns

### The Scope Model

Every piece of data has a `Scope`:
- `Platform` — cross-tenant data, `tenant_id = NULL`
- `Tenant` — tenant-specific data, `tenant_id` is set

Every write MUST specify a `ScopeContext`. Every read MUST respect scope boundaries.

### Resolving Scope in ASP.NET Core

```csharp
// Implement IScopeResolver — determines scope from HTTP request
public sealed class JwtScopeResolver : IScopeResolver
{
    public ScopeContext Resolve(HttpContext context)
    {
        var scopeClaim = context.User.FindFirst("scope")?.Value;
        var tenantClaim = context.User.FindFirst("tenant_id")?.Value;

        return scopeClaim switch
        {
            "Platform" => ScopeContext.Platform(),
            "Tenant" when tenantClaim is not null => ScopeContext.Tenant(tenantClaim),
            _ => throw new UnauthorizedAccessException("Invalid or missing scope claim.")
        };
    }
}

// Register
builder.Services.AddPapumaScope<JwtScopeResolver>();
app.UseScopeResolution();

// Use in controller
[HttpPost]
public async Task<IActionResult> Create(CreateOrderCommand cmd, CancellationToken ct)
{
    var scope = HttpContext.GetScopeContext();
    await _handler.HandleAsync(scope, cmd, HttpContext.GetActorId(), ct);
    return Ok();
}
```

### How RLS Works

`ScopeConnectionExtensions.SetScopeAsync()` sets `app.current_scope` and `app.current_tenant` as `SET LOCAL` session variables. PostgreSQL RLS policies on framework tables use these to enforce scope isolation. Every writer (`ChangeWriter`, `BusinessEventWriter`, `OutboxWriter`) calls `SetScopeAsync` before writing.

### Actor ID Resolution

```csharp
// Extension method on HttpContext
public static class ActorIdExtensions
{
    public static string GetActorId(this HttpContext context)
    {
        // From JWT subject claim
        var sub = context.User.FindFirst("sub")?.Value;
        if (sub is not null) return $"user:{sub}";

        // From API key header
        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (apiKey is not null) return $"apikey:{apiKey[..8]}";

        return "anonymous";
    }
}
```

---

## 7. GDPR and Sensitive Data Patterns

### Redaction Flow

To redact an entity (GDPR Article 17 — Right to Erasure):

```csharp
public async Task<RedactionResult> RedactUserAsync(
    string userId, string adminActorId, string reason, CancellationToken ct)
{
    var scope = ScopeContext.Platform(); // or Tenant, depending on data location

    var result = await _gdprProcessor.RedactEntityAsync(
        scope,
        entity: "UserProfile",
        entityId: userId,
        actorId: adminActorId,
        reason: reason, // e.g. "GDPR Art. 17 request #REQ-2026-042"
        ct: ct);

    // result.FeedEventsRedacted — how many change_feed rows were redacted
    // result.BusinessEventsRedacted — how many business_event_log rows were redacted
    // An 'EntityRedacted' audit event is written automatically

    return result;
}
```

**What happens internally**:
1. All `change_feed` rows for that entity/entityId get `payload = {"redacted": true}`, `redacted = TRUE`
2. All `business_event_log` rows for that entity/entityId get `payload = {"redacted": true}`, `redacted = TRUE`
3. An `EntityRedacted` business event is written as audit trail

### Sensitive Data Indirection (for PII)

For high-risk PII (email, phone, address, medical data), NEVER embed the sensitive data directly in event payloads. Use `SensitiveRef` indirection:

**Write path**:
```csharp
// 1. Store sensitive data separately
var sensitiveRef = SensitiveRef.New();
await _sensitiveStore.AppendAsync(
    scope,
    sensitiveRef,
    schemaVersion: 1,
    payloadJson: """{"email":"alice@example.com","phone":"+49..."}""",
    actorId: actorId,
    ct: ct);

// 2. Event payload contains ONLY the reference
await _changeWriter.AppendAsync(
    tx, scope,
    entity: "Profile",
    entityId: userId,
    eventType: "ProfileEmailUpdated",
    version: 1,
    payloadJson: $$"""{"sensitiveRef":"{{sensitiveRef.Value}}"}""",
    actorId: actorId,
    ct: ct);
```

**Read path** (projection or query handler):
```csharp
// Resolve only when authorized to see the data
var payload = await _sensitiveResolver.TryResolveLatestPayloadAsync(
    scope, sensitiveRef, ct);
// Returns null if redacted, deleted, or not found
```

**Lifecycle operations**:
```csharp
await _sensitiveStore.MarkRedactedAsync(scope, ref, actorId, "GDPR Art. 17", ct);
await _sensitiveStore.MarkDeletedAsync(scope, ref, actorId, "GDPR Art. 17", ct);
await _sensitiveStore.SetLegalHoldAsync(scope, ref, enabled: true, actorId, "Legal case #123", ct);
```

### Sensitive Store: Transaction Context

`ISensitiveDataStore.AppendAsync()` runs **outside the main write transaction** — it has no `NpgsqlTransaction` parameter. This is intentional: sensitive data is stored independently so it can be redacted without touching the event stream.

**Implication**: If the main transaction rolls back after `AppendAsync` has already written to `sensitive_data_versions`, the sensitive record exists but no event references it. This is safe — an orphaned sensitive record causes no harm and will not be resolved by any projection. Do not attempt to manually compensate for this.

**Correct pattern**:
```csharp
// 1. Store sensitive data FIRST (outside transaction)
var sensitiveRef = SensitiveRef.New();
await _sensitiveStore.AppendAsync(scope, sensitiveRef, schemaVersion: 1,
    payloadJson: """{"email":"alice@example.com"}""", actorId: actorId, ct: ct);

// 2. Then open transaction and write event with reference
await _uow.ExecuteAsync(async (conn, tx, ct) =>
{
    // domain write ...
    await _changeWriter.AppendAsync(tx, scope, "Profile", userId,
        "ProfileEmailUpdated", version: 1,
        payloadJson: $$"""{"sensitiveRef":"{{sensitiveRef.Value}}"}""",
        actorId: actorId, ct: ct);
}, ct);
```

### Sensitive Store: Schema Versioning

The `schemaVersion` parameter in `AppendAsync` tracks the shape of the sensitive payload. When the PII schema evolves (e.g., `phone` becomes `phoneNumbers[]`), increment `schemaVersion` and handle both versions in the resolver.

```csharp
// Writing with schema version 2 (new shape)
await _sensitiveStore.AppendAsync(scope, sensitiveRef, schemaVersion: 2,
    payloadJson: """{"email":"alice@example.com","phoneNumbers":["+49..."]}""",
    actorId: actorId, ct: ct);

// Resolving — check schemaVersion to deserialize correctly
var latest = await _sensitiveStore.GetLatestAsync(scope, sensitiveRef, ct);
if (latest is { State: SensitiveDataState.Active })
{
    var pii = latest.SchemaVersion switch
    {
        1 => DeserializeV1(latest.PayloadJson),
        2 => DeserializeV2(latest.PayloadJson),
        _ => throw new InvalidOperationException($"Unknown schema version {latest.SchemaVersion}")
    };
}
```

### SensitiveRef in Outbox Context

When an event with a `sensitiveRef` is enqueued in the outbox, the external consumer faces a choice: receive the reference and resolve it themselves, or receive the resolved data.

**Option A — Pass the reference (preferred for trusted internal consumers)**:
```csharp
// Outbox payload contains only the reference
await _outboxWriter.EnqueueAsync(tx, scope,
    eventId: eventId,
    eventType: "ProfileEmailUpdated",
    payloadJson: $$"""{"userId":"{{userId}}","sensitiveRef":"{{sensitiveRef.Value}}"}""",
    ct: ct);
// Consumer must have access to the Sensitive Store API to resolve
```

**Option B — Resolve before enqueue (for external/untrusted consumers)**:
```csharp
// Resolve PII before building outbox payload — outside the transaction
var piiJson = await _sensitiveResolver.TryResolveLatestPayloadAsync(scope, sensitiveRef, ct);
if (piiJson is null) return; // already redacted — do not enqueue

// Enqueue with resolved data (consumer gets the actual PII)
await _outboxWriter.EnqueueAsync(tx, scope,
    eventId: eventId,
    eventType: "ProfileEmailUpdated",
    payloadJson: $$"""{"userId":"{{userId}}","email":{{piiJson}}}""",
    ct: ct);
// WARNING: resolved PII is now in event_outbox — ensure the external system handles it under GDPR
```

**Decision rule**:
- Internal consumers with access to the Sensitive Store → pass the `sensitiveRef`
- External consumers (third-party APIs, webhooks) → resolve before enqueue, but document the GDPR implications

### Retention

Redacted records are eventually deleted by `RetentionWorker`:

```csharp
builder.Services.AddRetentionWorker(options =>
{
    options.RetentionWindow = TimeSpan.FromDays(365);
    options.BatchSize = 1000;
    options.DeleteFromChangeFeed = true;
    options.DeleteFromBusinessEventLog = false;  // Keep audit trail longer
}, scopeFilter: ScopeFilter.Platform());
```

---

## 8. Outbox Pattern

### Implementing IOutboxPublisher

```csharp
public sealed class RabbitMqOutboxPublisher : IOutboxPublisher
{
    public async Task<bool> PublishAsync(
        ScopeContext scope,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct)
    {
        try
        {
            var properties = new BasicProperties
            {
                MessageId = eventId.ToString(),  // Used for deduplication
                Type = eventType,
                Headers = new Dictionary<string, object?>
                {
                    ["scope"] = scope.Scope.ToString(),
                    ["tenant_id"] = scope.TenantId ?? ""
                }
            };

            await _channel.BasicPublishAsync(
                exchange: "papuma.events",
                routingKey: eventType,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(payloadJson),
                cancellationToken: ct);

            return true;  // Success
        }
        catch (Exception)
        {
            return false; // Will be retried by OutboxWorker
        }
    }
}
```

**Key rules**:
- `PublishAsync` must be **idempotent** — the `OutboxWorker` delivers at-least-once.
- Use `eventId` (Guid) as a deduplication key at the broker/consumer side.
- Return `true` on success, `false` on transient failure (triggers retry with backoff).
- Throw only on non-retryable failures.

The `OutboxWorker` handles:
- Polling `event_outbox` for pending messages
- Exponential backoff retry (base 5s, max 5min, up to 10 attempts)
- Dead-lettering after max attempts
- Scope filtering

### Outbox Payload Design

The outbox payload is an **integration contract** — it is consumed by external systems that you do not control. Design it explicitly for the consumer, not as a copy of the internal event payload.

**Key principles**:
- Build the outbox payload from the consumer's perspective, not from the internal domain model
- Different external consumers may need different payloads for the same domain event — enqueue separately with tailored payloads
- Do not include internal IDs, framework artefacts, or fields the consumer cannot interpret
- Apply the GDPR minimum-data principle: include only PII the consumer is authorized to receive

**Example: one domain event, two outbox messages**:
```csharp
var orderPayload = $$"""{"orderId":"{{orderId}}","amount":{{amount}},"currency":"EUR"}""";
var fulfillmentPayload = $$"""{"orderId":"{{orderId}}","shippingAddress":{{shippingAddressJson}},"items":{{itemsJson}}}""";

// Enqueue for analytics (amount only, no PII)
var analyticsEventId = await _eventWriter.AppendAsync(tx, scope,
    eventType: "OrderPlaced", actorId: actorId, payloadJson: orderPayload, ct: ct);
await _outboxWriter.EnqueueAsync(tx, scope,
    eventId: analyticsEventId, eventType: "OrderPlaced",
    payloadJson: orderPayload, ct: ct);

// Enqueue for fulfillment (shipping details, no financial data)
var fulfillmentEventId = await _eventWriter.AppendAsync(tx, scope,
    eventType: "OrderReadyForFulfillment", actorId: actorId, payloadJson: fulfillmentPayload, ct: ct);
await _outboxWriter.EnqueueAsync(tx, scope,
    eventId: fulfillmentEventId, eventType: "OrderReadyForFulfillment",
    payloadJson: fulfillmentPayload, ct: ct);
```

**Anti-pattern**: Do not reuse the `change_feed` payload as the outbox payload by default. The `change_feed` payload is optimized for projection replay; the outbox payload is optimized for external consumption. They serve different purposes and will diverge over time.

---

## 9. Testing Patterns

### Unit Testing a Write Handler

```csharp
[Fact]
public async Task HandleAsync_ValidCommand_WritesToChangeFeed()
{
    // Arrange
    var tx = Substitute.For<NpgsqlTransaction>();
    var conn = Substitute.For<NpgsqlConnection>();
    tx.Connection.Returns(conn);

    var uow = Substitute.For<IUnitOfWork>();
    uow.ExecuteAsync(Arg.Any<Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>>(),
                     Arg.Any<CancellationToken>())
       .Returns(callInfo =>
       {
           var action = callInfo.ArgAt<Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>>(0);
           return action(conn, tx, CancellationToken.None);
       });

    var changeWriter = Substitute.For<ChangeWriter>();

    var handler = new CreateOrderHandler(uow, changeWriter, ...);

    // Act
    await handler.HandleAsync(
        ScopeContext.Tenant("acme"),
        new CreateOrderCommand("order-1", "cust-1", 42.00m),
        "user:test",
        CancellationToken.None);

    // Assert
    await changeWriter.Received(1).AppendAsync(
        tx,
        Arg.Is<ScopeContext>(s => s.Scope == ScopeType.Tenant && s.TenantId == "acme"),
        "Order",
        "order-1",
        "OrderCreated",
        1,
        Arg.Any<string>(),
        "user:test",
        Arg.Any<string>(),
        Arg.Any<string>(),
        Arg.Any<string>(),
        Arg.Any<CancellationToken>());
}
```

### Testing Idempotency

```csharp
[Fact]
public async Task AppendAsync_SameIdempotencyKeyTwice_DoesNotThrow()
{
    // Append first time
    await changeWriter.AppendAsync(tx, scope, "Order", "1", "OrderCreated",
        1, "{}", "user:test", idempotencyKey: "key-123");

    // Append second time with same key — should not throw
    var ex = await Record.ExceptionAsync(() =>
        changeWriter.AppendAsync(tx, scope, "Order", "1", "OrderCreated",
            1, "{}", "user:test", idempotencyKey: "key-123"));

    Assert.Null(ex);
}
```

### Integration Tests

Use **Testcontainers for PostgreSQL** to run real integration tests:

```csharp
public class DatabaseFixture : IAsyncLifetime
{
    public NpgsqlDataSource DataSource { get; private set; }

    public async Task InitializeAsync()
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .Build();
        await container.StartAsync();

        var builder = new NpgsqlDataSourceBuilder(container.GetConnectionString());
        DataSource = builder.Build();

        // Apply schema migrations
        await ApplyMigrationsAsync(DataSource);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
    }
}
```

---

## 10. Design Patterns & Decision Trees

### When to create a new Projection vs. extend an existing one?

```
You need a new read model.
   │
   ├─ Serves the same consumer/app as an existing projection?
   │    YES → Extend existing projection (add EventType to EventTypes, add case in HandleAsync)
   │
   ├─ Serves a different consumer/app?
   │    YES → New projection (different consumer = different visibility/security needs)
   │
   └─ Has different SLA/latency requirements?
        YES → New projection (can be scaled independently)
```

### When to use External Projection vs. DB Projection?

| Criterion | DB Projection | External Projection |
|-----------|---------------|---------------------|
| Target | PostgreSQL | Elasticsearch, Redis, API, file |
| Transactional safety | Yes (atomic with checkpoint) | No (must be idempotent) |
| Complexity | Lower | Higher (dedup required) |
| Use case | App read models, dashboards | Search, cache, external integrations |

### When to use IReplayableProjection?

Implement `IReplayableProjection` when:
- Your read model CAN be truncated and rebuilt from scratch
- You expect to evolve the projection logic over time
- You want to be able to trigger manual replays after fixing bugs

Do NOT implement it when:
- Your read model accumulates data that can't be reconstructed from the feed alone
- The projection writes to an external system that can't be truncated

### When to use SensitiveRef vs. embedding data in payload?

| Criterion | Embed in payload | Use SensitiveRef |
|-----------|-----------------|------------------|
| Data type | Non-sensitive | PII, financial, medical |
| GDPR relevance | No | Yes |
| Audit exposure | Fine | Must be limited |
| Read frequency | Every read needs it | Only privileged reads resolve it |

**Rule of thumb**: If the data would need to be redacted under GDPR — use `SensitiveRef`.

---

## 11. Anti-Patterns Checklist

When reviewing code before committing, verify these are NOT present:

| # | Anti-Pattern | Why it's wrong | Fix |
|---|-------------|----------------|-----|
| 1 | **Synchronous projection in write transaction** | Couples write latency to projection speed, creates cascading failures | Projections are async workers, always |
| 2 | **Missing actorId** | Audit trail broken, GDPR compliance impossible | `actorId` is always required |
| 3 | **Writing state-change events only to business_event_log** | Read models can't be rebuilt from business events | State changes go to `change_feed` |
| 4 | **Writing signal-only events to change_feed** | Bloats feed, slows replays, confuses projections | Signals go to `business_event_log` |
| 5 | **Non-idempotent projection handler** | Replay produces duplicates, at-least-once breaks | Use `ON CONFLICT`, dedup keys |
| 6 | **Embedding sensitive data in event payloads** | GDPR redaction leaves PII in too many places | Use `SensitiveRef` indirection |
| 7 | **Silent GDPR deletion without audit** | Compliance violation, no forensics possible | Always use `GdprProcessor.RedactEntityAsync` with reason + actorId |
| 8 | **Missing scope context on writes** | Tenant isolation broken, RLS can't work | Every write requires a `ScopeContext` |
| 9 | **Hardcoding tenant IDs** | Fragile, breaks in multi-tenant deploys | Resolve from request context via `IScopeResolver` |
| 10 | **Sharing projections across apps with different security needs** | Data leaks between apps with different permissions | One projection per consumer |
| 11 | **Mixing Platform and Tenant data in one projection without scope filter** | Security boundary violation | Use explicit `ScopeFilter` on worker registration |
| 12 | **Skipping idempotencyKey on retryable commands** | Duplicate events on retry | Pass stable idempotency keys |
| 13 | **Copying change_feed payload blindly into outbox** | External consumers get internal fields they can't interpret; contracts diverge silently | Build outbox payload explicitly for each external consumer |
| 14 | **Copying change_feed payload blindly into business_event_log** | Bloats log with unnecessary data; increases GDPR surface area | Use selective, purpose-specific payload in business_event_log |
| 15 | **Calling ISensitiveDataStore.AppendAsync inside the main transaction** | Sensitive store has no transaction parameter — the call runs outside; misunderstanding leads to incorrect rollback assumptions | Call AppendAsync before opening the main transaction |
| 16 | **Sending resolved PII to external consumers without GDPR documentation** | PII leaves the controlled sensitive store boundary without audit trail | Document GDPR basis for each external consumer receiving resolved PII; prefer passing sensitiveRef to internal consumers |

---

## 12. Schema Version Check

Always add this at startup to fail fast on migration drift:

```csharp
// Program.cs — after building app, before Run()
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var checker = scope.ServiceProvider.GetRequiredService<SchemaVersionChecker>();
    await checker.EnsureCurrentBaselineAsync();
    // Throws InvalidOperationException if DB schema is behind version 4
}

app.Run();
```

Current required baseline: **version 4**.

---

## 13. Health Checks

Add projection lag monitoring to your health check endpoint:

```csharp
builder.Services.AddPapumaProjectionHealthChecks(options =>
{
    options.MaxAllowedLag = 500; // unhealthy if > 500 events behind
});

var app = builder.Build();
app.MapHealthChecks("/health");
```

The check reports unhealthy when ANY projection's lag exceeds the threshold. Use this in your orchestration layer (Kubernetes liveness/readiness probes, load balancer health checks).

---

## 14. Complete Host Wiring Template

This is a complete `Program.cs` for a typical Papuma.Kernel application:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Papuma.Kernel;
using Papuma.Kernel.Events;
using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Projections;
using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.AspNetCore.Projections;
using Papuma.Kernel.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// === DATABASE ===
builder.Services.AddSingleton(sp =>
{
    var cs = builder.Configuration.GetConnectionString("papuma")
        ?? throw new InvalidOperationException("Connection string 'papuma' is missing.");
    return new NpgsqlDataSourceBuilder(cs).Build();
});

// === KERNEL ===
builder.Services.AddPapumaKernel(options =>
{
    options.MaxPayloadSizeBytes = 256 * 1024;
    options.UnitOfWork = new UnitOfWorkOptions { MaxRetries = 5 };
});

// === PROJECTIONS ===
builder.Services.AddProjection<OrderSummaryProjection>(
    scopeFilter: ScopeFilter.Tenant("acme"));

builder.Services.AddExternalProjection<SearchIndexProjection>(
    scopeFilter: ScopeFilter.Platform());

builder.Services.AddReplayService();

// === OUTBOX ===
builder.Services.AddOutboxWorker<RabbitMqOutboxPublisher>(
    configure: options =>
    {
        options.BatchSize = 100;
        options.PollInterval = TimeSpan.FromSeconds(2);
    },
    scopeFilter: ScopeFilter.All());

// === RETENTION (GDPR cleanup) ===
builder.Services.AddRetentionWorker(
    configure: options =>
    {
        options.RetentionWindow = TimeSpan.FromDays(365);
        options.BatchSize = 1000;
    },
    scopeFilter: ScopeFilter.Platform());

// === SCOPE RESOLUTION (multi-tenancy) ===
builder.Services.AddPapumaScope<JwtScopeResolver>();

// === HEALTH CHECKS ===
builder.Services.AddPapumaProjectionHealthChecks(options =>
{
    options.MaxAllowedLag = 500;
});

var app = builder.Build();

// === MIDDLEWARE ===
app.UseScopeResolution();
app.MapHealthChecks("/health");

// === SCHEMA CHECK ===
using (var startupScope = app.Services.CreateScope())
{
    var checker = startupScope.ServiceProvider.GetRequiredService<SchemaVersionChecker>();
    await checker.EnsureCurrentBaselineAsync();
}

app.Run();
```

---

## 15. Quick Reference Card for AI Agents

When implementing a feature on Papuma.Kernel, follow this checklist:

**Write path**:
- [ ] Domain state written in same transaction as events
- [ ] `ChangeWriter.AppendAsync()` called for every state change
- [ ] `BusinessEventWriter.AppendAsync()` called for business signals
- [ ] `OutboxWriter.EnqueueAsync()` called if external systems need notification
- [ ] `ScopeContext` explicitly passed to all writers
- [ ] `actorId` is always set (never null, never empty)
- [ ] `idempotencyKey` passed for retryable operations

**Payload design**:
- [ ] `change_feed` payload is complete (everything projections need for replay)
- [ ] `business_event_log` payload is selective (only what the signal consumer needs)
- [ ] Outbox payload is contract-driven (only what the external consumer's API requires)
- [ ] Different external consumers get separate, tailored outbox messages
- [ ] No `change_feed` payload blindly reused as outbox payload

**Projections**:
- [ ] Handler is idempotent (`ON CONFLICT DO UPDATE/NOTHING`)
- [ ] Handler uses the provided `connection` and `transaction`
- [ ] `EventTypes` correctly declared
- [ ] Registered with correct `ScopeFilter`
- [ ] Implements `IReplayableProjection` if replayable

**GDPR**:
- [ ] Sensitive PII uses `SensitiveRef` indirection
- [ ] `ISensitiveDataStore.AppendAsync()` called before the main transaction (not inside it)
- [ ] `schemaVersion` incremented when sensitive payload shape changes
- [ ] Outbox payload with PII: pass `sensitiveRef` to internal consumers, resolve for external
- [ ] Redaction uses `GdprProcessor.RedactEntityAsync` with reason and actorId
- [ ] Retention worker registered for cleanup

**Multi-tenancy**:
- [ ] `IScopeResolver` implemented for HTTP scope resolution
- [ ] Workers have explicit `ScopeFilter`
- [ ] No tenant ID hardcoded

**Operational**:
- [ ] `SchemaVersionChecker.EnsureCurrentBaselineAsync()` at startup
- [ ] Health checks include projection lag
- [ ] Connection pool sized for N projections + HTTP headroom

---

> **API reference**: [papuma-kernel-api-reference.md](papuma-kernel-api-reference.md)
> **Architecture context**: [papuma-kernel-ai-framework-context.md](papuma-kernel-ai-framework-context.md)
