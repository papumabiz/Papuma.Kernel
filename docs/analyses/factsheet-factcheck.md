# Factsheet Fact-Check — Papuma Kernel

This document cross-references every claim in [`factsheet.md`](../marketing/factsheet.md) against the actual source code and schema. Each claim is rated:

- ✅ **Confirmed** — fully implemented and verifiable in code
- ⚠️ **Partial** — the feature exists but with notable gaps or caveats
- ❌ **Incorrect / Missing** — the claim does not match the codebase

---

## 1. Core Claim: CRUD Truth + Change Feed

> *"Every write appends a structured record to `change_feed` in the same database transaction."*

✅ **Confirmed.**  
[`ChangeWriter.AppendAsync()`](../../src/Papuma.Kernel/ChangeFeed/ChangeWriter.cs:41) takes an `NpgsqlTransaction` as its first parameter and inserts into `change_feed` within that transaction. The schema ([`schema.sql`](../../src/Papuma.Kernel/Schema/schema.sql:4)) defines `change_feed` with `BIGSERIAL` sequence, `scope`, `tenant_id`, `entity`, `entity_id`, `event_type`, `version`, `correlation_id`, `causation_id`, `actor_id`, `payload` (JSONB), `timestamp`, and `redacted`.

**Caveat:** The framework provides the `ChangeWriter` — it does not enforce that the developer actually calls it alongside their CRUD mutation. The atomicity guarantee only holds if the caller uses the same `NpgsqlTransaction` for both. This is a usage contract, not a framework invariant.

---

## 2. Independent Projection Workers

> *"Each projection maintains its own checkpoint. Workers run independently, recover from crashes without replaying from the beginning, and can be replayed deliberately."*

✅ **Confirmed.**  
[`ProjectionWorker`](../../src/Papuma.Kernel/Projections/ProjectionWorker.cs:19) is a `BackgroundService` that:
- Persists checkpoints per projection name in `projection_checkpoint` ([`schema.sql`](../../src/Papuma.Kernel/Schema/schema.sql:64))
- Filters `change_feed` by `sequence_id > @lastSeen` and `redacted = FALSE`
- Implements exponential-backoff retry via `projection_failures` table ([`schema.sql`](../../src/Papuma.Kernel/Schema/schema.sql:71))
- Moves events to a dead-letter state after `MaxAttemptsPerEvent` failures (checkpoint is advanced, event is skipped)
- Supports replay via [`RequestReplayAsync()`](../../src/Papuma.Kernel/Projections/ProjectionWorker.cs:73), which resets the checkpoint to 0 and clears failures

**Notable detail not in the factsheet:** The worker uses a visibility guard (`xmin < pg_snapshot_xmin(pg_current_snapshot())`) to avoid reading uncommitted rows — a production-grade detail.

---

## 3. Business Event Log

> *"A separate `business_event_log` captures domain signals that are not state mutations."*

✅ **Confirmed.**  
[`BusinessEventWriter.AppendAsync()`](../../src/Papuma.Kernel/Events/BusinessEventWriter.cs:41) writes to `business_event_log`. The table ([`schema.sql`](../../src/Papuma.Kernel/Schema/schema.sql:84)) has its own `event_id` (UUID), `scope`, `tenant_id`, `event_type`, `entity` (optional), `entity_id` (optional), `actor_id`, `correlation_id`, `causation_id`, `payload`, `occurred_at`, and `redacted`. RLS is enabled on this table as well.

---

## 4. GDPR — Right to Erasure (Art. 17)

> *"Atomic redaction of all personal data across both the change feed and the business event log in a single transaction."*

✅ **Confirmed.**  
[`GdprProcessor.RedactEntityAsync()`](../../src/Papuma.Kernel/Gdpr/GdprProcessor.cs:84) opens one connection, begins one transaction, updates both `change_feed` and `business_event_log` (setting `payload = '{"redacted": true}'::jsonb` and `redacted = TRUE`), then appends an `EntityRedacted` audit event via `BusinessEventWriter`, and commits — all in one transaction.

`actorId` and `reason` are validated as non-empty strings before any database work is done.

---

## 5. GDPR — Right of Access (Art. 15)

> *"Full entity history retrieval across both tables."*

✅ **Confirmed.**  
[`GdprProcessor.GetEntityHistoryAsync()`](../../src/Papuma.Kernel/Gdpr/GdprProcessor.cs:181) queries both `change_feed` and `business_event_log` for a given `(scope, entity, entityId)` and returns an [`EntityHistory`](../../src/Papuma.Kernel/Gdpr/EntityHistory.cs) record containing both lists. Redacted entries are included (the query does not filter by `redacted`), which is correct for Art. 15 — the subject has the right to know that data existed.

---

## 6. Multi-Tenancy / Scope Model

> *"An explicit Scope Model (`Platform` / `Tenant`) is baked into every write API, every table, and every projection checkpoint."*

✅ **Confirmed.**  
- [`ScopeContext`](../../src/Papuma.Kernel/Tenancy/ScopeContext.cs:11) is a sealed record with factory methods `Platform()` and `Tenant(tenantId)`. `Tenant()` validates the `tenantId` against a compiled regex (`^[A-Za-z][A-Za-z0-9_]{1,100}$`).
- All three write APIs (`ChangeWriter`, `BusinessEventWriter`, `OutboxWriter`) require a `ScopeContext` parameter and call `SET LOCAL app.current_scope` / `SET LOCAL app.current_tenant` before inserting.
- Polling workers (`ProjectionWorker`, `ExternalProjectionWorker`, `OutboxWorker`) also set scope session variables before reading RLS-protected tables.
- All three tables have `CHECK` constraints enforcing the `Platform → tenant_id IS NULL` / `Tenant → tenant_id IS NOT NULL` invariant.
- RLS policies are defined in [`schema.sql`](../../src/Papuma.Kernel/Schema/schema.sql:37) for all three tables.
- Projection checkpoint names include the scope: `{HandlerName}@Platform` or `{HandlerName}@Tenant:{TenantId}` ([`ProjectionWorker`](../../src/Papuma.Kernel/Projections/ProjectionWorker.cs:60)).

**Caveat:** RLS is defined in the schema but the framework does not ship a migration runner or enforce that the schema is applied. The developer must apply [`schema.sql`](../../src/Papuma.Kernel/Schema/schema.sql) manually.

---

## 7. Outbox Pattern

> *"An `event_outbox` table and `OutboxWriter` enable reliable integration with external systems."*

✅ **Confirmed.**  
[`OutboxWriter.EnqueueAsync()`](../../src/Papuma.Kernel/Events/OutboxWriter.cs:36) writes to `event_outbox` within a transaction. The table ([`schema.sql`](../../src/Papuma.Kernel/Schema/schema.sql:140)) has `status` (`Pending`/`Sent`/`Failed`), `attempts`, `next_retry_at`, and `last_error` columns — a complete outbox schema.

The library now includes [`OutboxWorker`](../../src/Papuma.Kernel/Events/OutboxWorker.cs), which polls retry-eligible outbox rows and dispatches them via [`IOutboxPublisher.PublishAsync(...)`](../../src/Papuma.Kernel/Events/IOutboxPublisher.cs:25). Registration is provided through [`AddOutboxWorker<TPublisher>()`](../../src/Papuma.Kernel/Events/OutboxExtensions.cs:27).

The application still owns the concrete publisher implementation (broker/webhook adapter), while dispatch orchestration is now part of the kernel.

---

## 8. ASP.NET Core Integration

> *"`IScopeResolver` and `ScopeMiddleware` for resolving the current scope from HTTP context."*

✅ **Confirmed.**  
[`ScopeMiddleware`](../../src/Papuma.Kernel.AspNetCore/Tenancy/ScopeMiddleware.cs:11) resolves `IScopeResolver` from DI, calls `Resolve(HttpContext)`, and stores the result in `HttpContext.Items`. [`ScopeMiddlewareExtensions`](../../src/Papuma.Kernel.AspNetCore/Tenancy/ScopeMiddlewareExtensions.cs) provides `AddPapumaScope<TResolver>()` and `UseScopeResolution()`.

**Caveat:** The factsheet's Getting Started example shows `options.ConnectionString = "..."` inside `AddPapumaKernel()`. In reality, [`PapumaKernelOptions`](../../src/Papuma.Kernel/PapumaKernelOptions.cs) has no `ConnectionString` property — it only exposes `MaxPayloadSizeBytes` and `UnitOfWork`. The `NpgsqlDataSource` must be registered separately by the developer (e.g. via `NpgsqlDataSourceBuilder`). **The Getting Started code example in the factsheet is incorrect.**

---

## 9. Schema Versioning / `IVersionedHandler`

> *"Every change feed entry carries a `version` field. Projections can implement `IVersionedHandler` to handle schema evolution."*

✅ **Confirmed.**  
The `version` column exists in `change_feed` (default `1`). [`IVersionedHandler<TPayload>`](../../src/Papuma.Kernel/Projections/IVersionedHandler.cs:12) is defined with a `Version` property and a `HandleAsync(TPayload, ChangeRecord, CancellationToken)` method.

**Caveat:** `IVersionedHandler<T>` is a standalone interface. There is no base class or dispatcher that automatically routes a `ChangeRecord` to the correct versioned handler based on `record.Version`. The developer must implement this dispatch logic themselves inside their `IProjectionHandler.HandleAsync()`.

---

## 10. Replay as a First-Class Operation

> *"The `ReplayService` makes this explicit and safe."*

✅ **Confirmed.**  
[`ReplayService.RequestReplayAsync(projectionName, ct)`](../../src/Papuma.Kernel/Projections/ReplayService.cs:28) looks up the named worker and calls `worker.RequestReplayAsync()`. The worker then resets the checkpoint to 0, clears all failure records, and optionally calls [`IReplayableProjection.PrepareReplayAsync()`](../../src/Papuma.Kernel/Projections/IReplayableProjection.cs:15) on the handler (e.g. to truncate a read model table before rebuilding).

[`ProjectionExtensions.AddReplayService()`](../../src/Papuma.Kernel/Projections/ProjectionExtensions.cs:58) auto-discovers all registered `ProjectionWorker` instances.

---

## 11. Unit of Work / Transient Retry

> *(Not explicitly mentioned in the factsheet, but registered via `AddPapumaKernel`.)*

✅ **Present and well-implemented.**  
[`NpgsqlUnitOfWork`](../../src/Papuma.Kernel/Transactions/NpgsqlUnitOfWork.cs:12) retries on PostgreSQL transient errors (`40001` serialization failure, `40P01` deadlock, `08006`/`08001`/`57P03` connection errors) with exponential backoff + jitter. This is a meaningful production feature that the factsheet does not mention at all.

---

## 12. Technology Stack Claims

| Factsheet claim | Reality |
|---|---|
| .NET 10 | ✅ `<TargetFramework>net10.0</TargetFramework>` in `.csproj` |
| PostgreSQL via Npgsql | ✅ Direct `NpgsqlDataSource` / `NpgsqlConnection` usage throughout |
| System.Text.Json | ✅ Used in `GdprProcessor` for audit payload serialization |
| Microsoft.Extensions.DependencyInjection | ✅ `ServiceCollectionExtensions`, `ProjectionExtensions` |
| Microsoft.Extensions.Hosting (BackgroundService) | ✅ `ProjectionWorker : BackgroundService` |
| MIT License | ✅ `LICENSE` file present; all source files carry the correct header |

---

## 13. "No message broker required"

> *"No message broker required. Just clean, auditable, GDPR-ready data flow."*

✅ **Accurate for the kernel itself.**  
All inter-component communication is via PostgreSQL polling. The outbox pattern is designed to integrate *with* a broker if needed, but the kernel itself has no broker dependency.

---

## Summary

| Area | Status | Notes |
|---|---|---|
| Change Feed write | ✅ | Atomicity is a usage contract, not enforced by the framework |
| Projection Workers | ✅ | Includes dead-letter, retry, visibility guard |
| Business Event Log | ✅ | Full feature parity with change feed |
| GDPR Art. 17 (erasure) | ✅ | Atomic, audited, both tables |
| GDPR Art. 15 (access) | ✅ | Both tables, includes redacted entries |
| Multi-Tenancy / Scope | ✅ | CHECK constraints + RLS + validated `ScopeContext` |
| Outbox (write side) | ✅ | Schema + `OutboxWriter` present |
| Outbox (delivery side) | ✅ | `OutboxWorker` + `IOutboxPublisher` contract |
| ASP.NET Core integration | ✅ | Middleware + extension methods present |
| Getting Started example | ❌ | `options.ConnectionString` does not exist in `PapumaKernelOptions` |
| Schema Versioning | ⚠️ | `IVersionedHandler<T>` exists; version dispatch must be hand-rolled |
| Replay | ✅ | `ReplayService` + `IReplayableProjection` |
| Unit of Work / retry | ✅ | Not mentioned in factsheet; present and production-grade |
| NuGet packages | ❌ | Factsheet shows `dotnet add package` — no NuGet packages are published |

---

## Recommended Factsheet Corrections

1. **Getting Started — remove `options.ConnectionString`.**  
   `PapumaKernelOptions` has no such property. Replace with a note that `NpgsqlDataSource` must be registered separately.

2. **Outbox — update to shipped delivery support.**  
   State explicitly that the kernel provides `OutboxWorker` and `AddOutboxWorker<TPublisher>()`, while the concrete `IOutboxPublisher` implementation remains application-specific.

3. **`IVersionedHandler` — clarify it is a contract, not a dispatcher.**  
   Add: *"Version routing within a handler must be implemented by the developer."*

4. **`dotnet add package` — remove or mark as future.**  
   The packages are not published to NuGet. Replace with a note about building from source or referencing the project directly.

5. **Add `NpgsqlUnitOfWork` to the feature list.**  
   Transient-retry with exponential backoff + jitter is a meaningful production feature worth advertising.
