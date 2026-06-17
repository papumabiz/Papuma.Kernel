# Papuma.Kernel — Playbook for AI Agents

Status: verified against the implemented API (phase 13, 2026-06-12) ·
Audience: coding agents that **build applications using Papuma.Kernel**.

This document is the entry map. The truth lives in the reference chain:
[getting-started.md](../vNEXT/getting-started.md) (the API in 5 minutes) →
[concepts.md](../vNEXT/concepts.md) (the why, §1–§20) →
[ADRs](../vNEXT/adr/) (binding decisions) →
[architecture.md](../vNEXT/architecture.md) (data model, namespaces).

## The mental model in four sentences

1. **The JSON document is the truth.** No event sourcing: state is stored
   directly (`papuma.document`), and the change feed is *derived* as a reversible
   field diff — atomically in the same statement (PostgreSQL ≥ 18,
   `RETURNING OLD/NEW`).
2. **The session is a unit of work.** All writes of a session commit atomically
   (`CommitAsync`) or not at all (dispose without commit = rollback).
3. **Reacting happens via feeds.** Change and event handlers consume strictly
   ordered, checkpointed, at-least-once. The kernel generates no projections —
   handlers are "dumb" and write wherever they want.
4. **Everything is scope-bound.** Every session belongs to a scope (`Platform` or
   `Tenant(id)`); isolation comes in two layers (explicit predicates + row level
   security).

## Hard rules (ADR prohibitions — non-negotiable)

| Rule | Why / source |
|---|---|
| `SaveAsync` **always** with `expectedVersion` (`0` = insert expected). On `ConcurrencyException`: reload, re-decide — no blind retry loops. | ADR-003; conflict UI: `GetHistoryAsync(id, fromVersion: expected + 1)` |
| **Put personal fields under a policy before the first save** (`[SensitiveData]`, `[TrackHash]`, `[DoNotTrack]` or fluent). Afterwards only `RedactHistoryAsync` helps (a sharp tool). | ADR-007/015; review: `DataInventory.Build(model).Documents[..].UnprotectedPaths` |
| **Do not invent or wish for a query DSL.** Reading goes via `LoadAsync`/`LoadByKeyAsync` (declared keys), SQL views (`security_invoker = on`!) or your own projections. | ADR-009; concepts §16 |
| **Never simulate a transforming schema change additively** (no "new field + keep the old one" for a rename). Renames/restructurings = `Upcast(fromVersion, …)`. Additive changes (new optional field, removed field) need *no* upcaster. | ADR-005, point 7 |
| **Handlers are idempotent** (at-least-once!) and **never block** — no waiting for humans/external answers inside a handler. Human-in-the-loop = write a task document, done. | ADR-009; concepts §18/§19 |
| **Checkpoint reset only for projections, never for effect handlers** (emails would be re-sent). The distinction is made when writing the handler. | concepts §19 |
| **Events only for facts without state truth** (`UserLoggedIn`). State transitions belong in the document; triggers in handlers. Events have no upcasting — a new shape = a new event type. | ADR-011/013 |
| **Bounded counters** (stock, quotas): `Increment` + a type validator — not load-check-save loops. | ADR-012; concepts §17 |
| **Conditional patches do not exist** and will not be added. Whoever needs conditions: load + save with `expectedVersion`. | ADR-012 |
| **Never write directly into `papuma.*` tables.** Reading via a view is legitimate (with the 4 caveats from concepts §16). | ADR-002 |
| Mass updates via `PatchWhereAsync`/`PatchManyAsync`/`DeleteWhereAsync` — not N sessions in a loop. | ADR-014 |

## API quick map

```csharp
// Bootstrap (application)
services.AddPapumaKernel(o => { o.ConnectionString = …; o.Model(m => m
    .Document<User>(d => d.UniqueKey(x => x.Email).Validate(u => …))
    .Event<UserLoggedIn>(e => e.Retention(TimeSpan.FromDays(90)))); })
  .AddChangeHandler<UserProjection>()
  .AddEventHandler<LoginAudit>();

// Writing (session = UoW; scope is mandatory)
await using var s = store.OpenSession(ScopeContext.Tenant("acme"),
    new SessionOptions { ActorId = "harry", CausationId = "cmd-42", CausationType = "PlaceOrder" });
await s.SaveAsync(doc, expectedVersion);            // 0 = insert
await s.PatchAsync<User>(id, p => p.Set(x => x.Name, "H").Increment(x => x.LoginCount));
await s.PatchWhereAsync<User>(x => x.Status, "old", p => p.Set(x => x.Status, "new"));
await s.DeleteAsync<User>(id, expectedVersion);
await s.RollbackAsync<User>(id, toVersion, expectedVersion);   // append-only
await s.AppendAsync(new UserLoggedIn(id, "web"));   // event, atomic with the writes
await s.CommitAsync();                              // otherwise: rollback on dispose

// Reading (strong consistency)
var r = await s.LoadAsync<User>(id);                // r.Document, r.Version
var byKey = await s.LoadByKeyAsync<User>(x => x.Email, "x@y.de");
var history = await s.GetHistoryAsync<User>(id, fromVersion: 3);

// Reacting
public sealed class UserProjection : IChangeHandler
{
    public string Name => "user-projection";        // = checkpoint identity!
    public Task HandleAsync(ChangeRecord change, CancellationToken ct) { … }
}

// Diagnostics (phase 11) · GDPR (phase 12)
await processor.GetLagAsync(); await processor.GetFailuresAsync();
await processor.RetryFailureAsync(name, seq); await processor.ResetCheckpointAsync(name);
DataInventory.Build(model);                         // Art. 30 + policy review
await GdprExport.ExportAsync(store, scope, docRefs, eventSelectors);
await s.RedactHistoryAsync<User>(id, reason, paths);
```

Typed errors you should handle (not swallow): `ConcurrencyException`,
`UniqueKeyViolationException`, `DocumentNotFoundException`,
`SchemaVersionConflictException` / `SchemaUpcastRequiredException`,
`RollbackNotPossibleException`.

## Decision tree: "I want to …"

| Need | Tool |
|---|---|
| …read with immediate consistency (login, business logic) | `LoadAsync` / `LoadByKeyAsync` |
| …query current data via SQL/BI | view with `security_invoker = on` (concepts §16) |
| …maintain a read model / search index | an `IChangeHandler` projection (eventual, lag observable) |
| …change a single field without loading | `PatchAsync` (field-level LWW is deliberate there) |
| …bound a stock (never oversell) | `Increment(-1)` + `Validate` (concepts §17) |
| …react to "field X went Y→Z" | `change.IsFieldTransition(path, from, to)` in a handler (ADR-011) |
| …add a human approval step | write a task document, handler returns; the decision = a normal write (concepts §18) |
| …build a workflow/saga | workflow document + feed handlers + `expectedVersion`; timers = a `dueAt` poller (concepts §18) |
| …update a UI live | the NOTIFY recipe ([realtime-ui-notifications](../vNEXT/recipes/realtime-ui-notifications.md)) |
| …embeddings/RAG, NL audit, anomaly detection | the AI recipes ([ai-consumers](../vNEXT/recipes/ai-consumers.md)) — feed consumers, not kernel features |
| …track who did what and why (audit) | `SessionOptions` with `ActorId`, `CausationType`, `CausationId` ([causation-tracking recipe](../vNEXT/recipes/causation-tracking.md)); ASP.NET Core: `GetSessionOptions()` with enricher |
| …serve a GDPR access/erasure request | [gdpr.md](../vNEXT/gdpr.md) — export, inventory, redaction |
| …inspect lag/failures of a running app | the diagnostics APIs ([observability.md](../vNEXT/observability.md)) or the MCP server (`Papuma.Kernel.Mcp`) |

## When something is stuck

- **Lag grows**: look at `papuma.feed.handler.duration` first — almost always a
  slow handler, not the engine (concepts §14: limits + escape routes).
- **Poison counter > 0**: `GetFailuresAsync()` → fix the cause →
  `RetryFailureAsync(handler, seq)`; if the checkpoint already passed it,
  additionally `ResetCheckpointAsync`.
- **`ConcurrencyException` piles up on one document**: a hot document. Check
  whether `PatchAsync` (independent fields) or `Increment` (counters) can replace
  the load-modify-save (concepts §4/§17).
- **RLS errors / empty reads in workers**: check the `'All'` scope mechanism
  (`SetAllScopesAsync` or `ScopeFilter.All()`), cross-cutting checklist in the
  [implementation plan](../vNEXT/implementation-plan.md).
- **Rollback fails typed**: diffs on the way back contain policy entries without
  values — that is intended (concepts §8); rethink the use case instead of
  removing the policy.

## What the kernel deliberately is NOT

No ORM, no query DSL, no workflow/BPMN engine, no projection generation, no event
sourcing, no LLM calls inside the kernel (deterministic infrastructure). If your
solution wants to "add one of these to the kernel": wrong turn — the answer is
almost certainly in an ADR or a concepts section.
