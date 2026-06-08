# Papuma.Kernel AI Framework Context

> For type signatures, method signatures, parameters, and code examples, see the companion [API Reference](papuma-kernel-api-reference.md).

This document is written for AI assistants and automation agents.
Its purpose is to provide a reliable architectural context for applications that should be built on top of Papuma.Kernel.

## 1. What Papuma.Kernel Is

Papuma.Kernel is a small .NET 10 framework library for PostgreSQL-based applications that need:

- explicit write-side facts (change events)
- asynchronous read-model projection
- multi-tenant scope isolation
- GDPR-compatible redaction workflows
- clear operational behavior without hidden framework magic

It is intentionally not full event sourcing. The primary source of truth is still CRUD state in domain tables. The event stream is used as a change feed for synchronization, replay, and audit.

Core principle:

> Writes commit facts. Projections converge asynchronously.

## 2. Design Philosophy

Papuma.Kernel prefers explicitness over abstraction.

- PostgreSQL + Npgsql instead of heavy ORM abstraction layers
- JSON/JSONB payloads for flexible schema evolution
- projection-side interpretation instead of global upcasting pipelines
- transaction boundaries controlled by application use cases
- audit and GDPR behavior implemented explicitly

## 3. Key Building Blocks

### Core services

- `ChangeWriter` writes change records (`AppendChangeAsync`, kind=Change) and business events (`AppendEventAsync`, kind=Event) into the unified `papuma_event_feed` table.
- `ChangeFeedReader` reads feed data.
- `OutboxWriter` writes integration messages to `papuma_event_outbox`.
- `GdprProcessor` performs redaction and history access operations.
- `SchemaVersionChecker` verifies schema baseline compatibility.
- `IUnitOfWork` (`NpgsqlUnitOfWork`) provides transaction + retry handling for transient DB errors.

### Worker/runtime services

- `ProjectionWorker` for DB projections.
- `ExternalProjectionWorker` for external systems.
- `OutboxWorker` for message publishing.
- `RetentionWorker` for retention cleanup.
- `ReplayService` for projection replay control.

### ASP.NET Core integration

- `AddPapumaScope<TResolver>()` registers scope resolution.
- `UseScopeResolution()` stores resolved `ScopeContext` per request.
- `HttpContext.GetScopeContext()` retrieves request scope.
- `AddPapumaProjectionHealthChecks()` adds projection lag health checks.

## 4. Conceptual Model

```mermaid
flowchart TD
    A[Command/API Request] --> B[Open transaction]
    B --> C[Write domain state]
    C --> D[Append to papuma_event_feed (Change/Event) and optional outbox]
    D --> E[Commit]
    E --> F[Return success]
    E --> G[Async workers]
    G --> H[Projection read models]
    G --> I[External integrations via outbox]
```

Important: projections are not part of the write transaction.

## 5. Scope and Multi-Tenancy Model

Papuma.Kernel uses explicit scopes:

- `ScopeType.Platform`
- `ScopeType.Tenant`

Runtime scope is represented by `ScopeContext`.

- `ScopeContext.Platform()`
- `ScopeContext.Tenant(tenantId)`

Workers can be restricted via `ScopeFilter`:

- `ScopeFilter.All()`
- `ScopeFilter.Platform()`
- `ScopeFilter.Tenant("acme")`

PostgreSQL RLS policies depend on session variables:

- `app.current_scope`
- `app.current_tenant`

These are set with `SET LOCAL` on the active transaction.

## 6. Database Model (High-Level)

Primary framework tables:

- `papuma_event_feed` — unified change and business event log (distinguished by `kind` column: `Change` or `Event`)
- `papuma_projection_checkpoint`
- `papuma_projection_failures`
- `papuma_event_outbox`
- `papuma_sensitive_data_versions`
- `papuma_schema_version`

Current required schema baseline is version `5`.

## 7. Event Classes and Their Roles

Both change records and business events live in the unified `papuma_event_feed` table, distinguished by the `kind` column:

- **Change** (`kind='Change'`): state transition facts used for replay and read-model rebuild. Written via `ChangeWriter.AppendChangeAsync()`.
- **Event** (`kind='Event'`): domain signals for process, analytics, notifications, integration semantics. Written via `ChangeWriter.AppendEventAsync()`.

Rule of thumb:

- if needed for state reconstruction, write a **Change** via `AppendChangeAsync`
- if meaningful as domain signal but not required for state reconstruction, write an **Event** via `AppendEventAsync`

## 8. Reliability and Idempotency Rules

Papuma.Kernel is designed with at-least-once processing semantics for workers.

Implications:

- projection handlers must be idempotent
- external side effects must be idempotent or deduplicated
- retried write paths should pass stable idempotency keys

The framework schema supports idempotency keys in:

- `papuma_event_feed` (unique index `ux_papuma_event_feed_idempotency_key` on scope + tenant + key)

## 9. GDPR and Sensitive Data Strategy

### Redaction model

GDPR handling is explicit and auditable:

- redact payloads by replacing content with a redaction marker
- set `redacted = TRUE`
- write an audit business event for the redaction action
- require actor and reason metadata

### Sensitive data store

For high-risk payloads, use reference indirection:

- put only a reference (`SensitiveRef`) into normal event payloads
- store sensitive content in `papuma_sensitive_data_versions`
- resolve via `ISensitiveDataResolver` only where needed

This reduces data sprawl and improves compliance handling.

## 10. Integration Surface for Applications

### Minimal DI bootstrap

```csharp
services.AddNpgsqlDataSource(connectionString);

services.AddPapumaKernel(options =>
{
    options.MaxPayloadSizeBytes = 256 * 1024;
    // options.UnitOfWork = new UnitOfWorkOptions { ... };
});
```

### Add projections and outbox

```csharp
services.AddProjection<MyProjectionHandler>(options =>
{
    options.BatchSize = 100;
    options.PollInterval = TimeSpan.FromMilliseconds(100);
});

services.AddExternalProjection<MyExternalProjectionHandler>();
services.AddOutboxWorker<MyOutboxPublisher>();
services.AddReplayService();
```

### Optional ASP.NET Core scope wiring

```csharp
services.AddPapumaScope<MyScopeResolver>();

app.UseScopeResolution();
```

## 11. AI Decision Rules (Use These During App Design)

When designing an application on Papuma.Kernel, an AI should follow these constraints:

1. Keep domain tables as primary state; do not model the app as pure event sourcing by default.
2. For each state mutation, append a change feed record in the same transaction.
3. Keep projections asynchronous and isolated from write-path latency.
4. Enforce explicit scope (`Platform` vs `Tenant`) in all write/read APIs.
5. Ensure projection and outbox handlers are idempotent.
6. Prefer payload references for sensitive PII; avoid copying PII into many events.
7. Treat `actorId` and audit metadata as required in write and GDPR-critical operations.
8. Use schema version checks at startup to fail fast on migration drift.
9. Avoid introducing Kafka/EventStore unless concrete scale constraints require it.
10. Prefer simple, debuggable PostgreSQL patterns over abstract framework magic.

## 12. Non-Goals and Anti-Patterns

Do not assume Papuma.Kernel is:

- a full CQRS/Event-Sourcing platform
- a message broker replacement for internet-scale fan-out
- an ORM abstraction framework
- an automatic magic projection orchestrator

Avoid these anti-patterns:

- synchronous projection updates in command transaction
- writing tenant data without explicit scope context
- non-idempotent handlers under retrying infrastructure
- storing raw sensitive data in every event payload
- silent GDPR deletion without auditable reason and actor

## 13. Suggested Prompt Snippet for Other Repositories

Use this snippet in another application repository to align AI behavior.

> **Ready-to-use file**: See [papuma-kernel-agents-snippet.md](papuma-kernel-agents-snippet.md) for a complete, copy-paste-ready `AGENTS.md` snippet including placement guidance and customization notes.

Minimal version:

```text
This application is built on Papuma.Kernel (.NET 10 + PostgreSQL).
Use a CRUD-truth + change-feed architecture.
Keep writes transactional (state write + change feed append).
Treat projections/outbox as asynchronous at-least-once workers and implement idempotency.
Use explicit ScopeContext (Platform/Tenant) and respect RLS session variables.
Use ChangeWriter.AppendEventAsync() for semantic domain signals (selective payload, not a copy of change payload).
Design outbox payloads as external consumer contracts, not copies of internal payloads.
Use GDPR redaction patterns and SensitiveRef indirection for high-risk PII.
Call ISensitiveDataStore.AppendAsync() before the main transaction, not inside it.
Do not introduce heavy event-sourcing abstractions unless explicitly requested.
```

## 14. Practical Notes for AI Agents

- Prefer small, local changes and preserve public API compatibility.
- Keep infrastructure code explicit and observable.
- Add tests for validation, idempotency, and failure/retry behavior.
- If introducing new workers, also define lag/error observability and health checks.

This context document is intended to be copied into dependent application repositories as a stable architecture briefing for AI-assisted development.