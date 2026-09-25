# Papuma.Kernel — Playbook for AI Agents

Status: verified against the implemented API (phase 13, 2026-06-12) ·
Audience: coding agents that **build applications using Papuma.Kernel** (this
page is Postgres-framed by default; the SQLite sibling's differences are
called out separately below).

This document is the entry map. The truth lives in the reference chain:
[getting-started.md](../getting-started.md) (the API in 5 minutes) →
[concepts.md](../concepts.md) (the why, §1–§31) →
[ADRs](../adr/) (binding decisions) →
[architecture.md](../architecture.md) (data model, namespaces).

## The mental model in four sentences

1. **The JSON document is the truth.** No event sourcing: state is stored
   directly (`papuma.document`), and the change feed is *derived* as a reversible
   field diff — atomically in the same statement (PostgreSQL ≥ 18,
   `RETURNING OLD/NEW`). On `Papuma.Kernel.Local` (SQLite) the same guarantee
   holds via a different mechanism — see "Differences" below.
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
| **Reading has a default order, not a free menu** (and never a query DSL): one current document → `LoadAsync`/`LoadByKeyAsync` (declared keys); anything derived → a projection (**the default**); an SQL view (`security_invoker = on`!) only as the ad-hoc/BI exception. "Real-time" alone is not a reason — that is `LoadByKeyAsync`. | ADR-009; concepts §16 |
| **Never simulate a transforming schema change additively** (no "new field + keep the old one" for a rename). Renames/restructurings = `Upcast(fromVersion, …)`. Additive changes (new optional field, removed field) need *no* upcaster. | ADR-005, point 7 |
| **Handlers are idempotent** (at-least-once!) and **never block** — no waiting for humans/external answers inside a handler. Human-in-the-loop = write a task document, done. | ADR-009; concepts §18/§19 |
| **Checkpoint reset only for projections, never for effect handlers** (emails would be re-sent). The distinction is made when writing the handler. | concepts §19 |
| **Events only for facts without state truth** (`UserLoggedIn`). State transitions belong in the document; triggers in handlers. Events have no upcasting — a new shape = a new event type. | ADR-011/013 |
| **Bounded counters** (stock, quotas): `Increment` + a type validator — not load-check-save loops. `Increment` is safe against concurrency, **not against duplicate commands**: a retried command counts twice. Make the command idempotent or use a set of ids instead of a counter. | ADR-012; concepts §17 |
| **Conditional patches do not exist** and will not be added. Whoever needs conditions: load + save with `expectedVersion`. | ADR-012 |
| **Never write directly into `papuma.*` tables.** Reading via a view is a deliberate exception (the 4 conditions from concepts §16) — the default derived read is a projection. | ADR-002 |
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
| …read one current document (login, business logic) | `LoadAsync` / `LoadByKeyAsync` (immediately consistent) |
| …**any derived read** — list, join, aggregation, search, external *(the default)* | an `IChangeHandler` projection (eventual, lag observable) |
| …keep tenant isolation on a projection table in the same database | RLS policy `USING (papuma.scope_visible(scope, tenant_id)) WITH CHECK (papuma.scope_writable(scope, tenant_id))`, handler sets `change.Scope` per change via `SetScopeAsync` — never copy the GUC names ([recipe](../recipes/same-database-read-models.md), ADR-019) |
| …ad-hoc SQL/BI, all 4 conditions met *(the exception)* | view with `security_invoker = on` (concepts §16) |
| …change a single field without loading | `PatchAsync` (field-level LWW is deliberate there) |
| …bound a stock (never oversell) | `Increment(-1)` + `Validate` (concepts §17) |
| …hand out sequential numbers (ticket keys, invoices) | `PatchAsync(id, p => p.Increment(x => x.Next))`, then `result.GetDocument<T>().Next` — atomic, no reload; make the command idempotent, or a retry issues a second number (concepts §17) |
| …enforce uniqueness over two fields ("number per project") | composite key `UniqueKey(x => new { x.ProjectId, x.Number })`; lookup `LoadByKeyAsync<T>(x => new { … }, ["p1", 42])` — no flattened helper field (ADR-020) |
| …use a GUID as tenant id | `ScopeContext.Tenant(guid)` — canonical lowercase dashed form; string ids match `^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$` and compare case-sensitively |
| …use the store in tests/tools without a host | `SchemaManager.EnsureSchemaAsync(dataSource, model)` + `new DocumentStore(dataSource, model)`; handlers via `ChangeFeedProcessor.ProcessOnceAsync()` ([getting-started §7](../getting-started.md#7-without-a-host-tests-tools-console-apps)) |
| …react to "field X went Y→Z" | `change.IsFieldTransition(path, from, to)` in a handler (ADR-011) |
| …add a human approval step | write a task document, handler returns; the decision = a normal write (concepts §18) |
| …build a workflow/saga | workflow document + feed handlers + `expectedVersion`; timers = a `dueAt` poller (concepts §18) |
| …update a UI live | the NOTIFY recipe ([realtime-ui-notifications](../recipes/realtime-ui-notifications.md)) |
| …embeddings/RAG, NL audit, anomaly detection | the AI recipes ([ai-consumers](../recipes/ai-consumers.md)) — feed consumers, not kernel features |
| …track who did what and why (audit) | `SessionOptions` with `ActorId`, `CausationType`, `CausationId` ([causation-tracking recipe](../recipes/causation-tracking.md)); ASP.NET Core: `GetSessionOptions()` with enricher |
| …serve a GDPR access/erasure request | [gdpr.md](../gdpr.md) — export, inventory, redaction |
| …inspect lag/failures of a running app | the diagnostics APIs ([observability.md](../observability.md)) or the MCP server (`Papuma.Kernel.Mcp`) |

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
  (`SetAllScopesAsync` or `ScopeFilter.All()`); why a missing scope yields empty
  reads instead of an error: [concepts.md §23](../concepts.md#23-why-tenant-isolation-fails-closed-and-why-two-layers-not-one).
- **Rollback fails typed**: diffs on the way back contain policy entries without
  values — that is intended (concepts §8); rethink the use case instead of
  removing the policy.

## Differences when using `Papuma.Kernel.Local` (SQLite, embedded)

Same model, same API shape (`SqliteDocumentSession` mirrors `DocumentSession`),
same hard rules above — with these engine-specific adjustments:

- **No PostgreSQL, no `papuma.*` schema.** Tables are unprefixed
  (`document`/`change`/`event`/`checkpoint`/`failure`); `RETURNING OLD/NEW`
  doesn't exist in SQLite — the session does a version-checked `SELECT` +
  `UPDATE...RETURNING` instead (two statements, still atomic — the
  transaction is exclusive, nothing can interleave).
- **No row-level security.** Isolation is explicit scope predicates only — a
  single-writer, single-process store has no second tenant's process to
  defend against. Still fail-closed: a missing scope yields empty reads.
- **No SQL-view read lens.** The Postgres exception ("ad-hoc/BI view with
  `security_invoker = on`") doesn't apply on SQLite — every derived read is a
  projection, no exception, ever.
- **No `FOR UPDATE SKIP LOCKED` leader election.** A single-writer store never
  runs concurrent processor instances, so there's nothing to elect a leader
  for.
- **Feed wakeup is in-process** (`SqliteChangeNotifier`, a bounded channel),
  not `LISTEN`/`NOTIFY` — the realtime-UI-notifications recipe's *pattern*
  still applies, its Postgres-specific wiring doesn't.
- **One writer, one file, one process.** Don't open the same `.db` file from
  two processes — that's an OS file-lock violation, not a kernel concern to
  code around.
- **No embedded dashboard, no feed-lag health check, no MCP surface (yet).**
  Those live in `Papuma.Kernel.AspNetCore`/`Papuma.Kernel.Mcp`, which have no
  SQLite counterpart. `KernelDiagnostics` (`Meter`/`ActivitySource`) *is*
  shared — both kernels' metrics/traces land in the same OTel pipeline.

Everything else on this page — `SaveAsync`/`PatchAsync`/`Increment`/handlers/
GDPR/schema evolution/scope-binding — applies unchanged; it's the same code,
shared via `Papuma.Kernel.Core`. Full reasoning for every difference above:
[docs/analyses/local-kernel-sqlite-sibling.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/analyses/local-kernel-sqlite-sibling.md)
(shipped inside the `Papuma.Kernel.Local` package).

## Using `Papuma.Kernel.FSharp` (F#, additive facade)

Not a third kernel — a thin, optional facade package that sits alongside
either `Papuma.Kernel` or `Papuma.Kernel.Local`. Everything above still
applies unchanged; this section is only what an F# consumer needs on top.
Full reasoning: [concepts.md §29](../concepts.md#29-f-as-a-facade-not-a-rewrite--and-why-the-wire-format-stays-closed);
slice-by-slice translation: [slice-conventions.md](papuma-kernel-slice-conventions.md#using-papumakernelfsharp-f).

- **`Patch` needs quotations, not lambdas.** `x => x.Field` is C#-compiler
  magic F# doesn't have. Use `SetQ`/`RemoveQ`/`IncrementQ` with
  `<@ fun x -> x.Field @>` instead of `Set`/`Remove`/`Increment`.
- **`trySaveAsync`/`tryPatchAsync` return `Result<'T, KernelError>`** for the
  three *expected* write outcomes (`VersionConflict`/`DocumentNotFound`/
  `UniqueKeyViolation`) instead of throwing — optional, not a replacement:
  `SaveAsync`/`PatchAsync` still work and still throw exactly as documented
  above if you'd rather catch.
- **`runSession`** fills the gap where F#'s `use` doesn't bind
  `IAsyncDisposable` (which is what a session implements) — use it instead of
  a hand-written `try`/`finally` + `DisposeAsync`.
- **Still true, no exception for F#: `CommitAsync` is not automatic.** A
  session's writes are invisible to every other session, and silently rolled
  back on dispose, until `CommitAsync` is called. Easy to miss for what
  "looks" like a single, already-done write — see the comment in
  `samples/fsharp-local-todo/Program.fs`, where this was the one real bug
  the sample shipped with before it was caught by actually running it.
- **Hard rule, no exception: no discriminated-union or `option` fields on
  document types.** The kernel's JSON serializer config is fixed on purpose
  (one deterministic wire format for every language/process reading the
  feed) and has no converter extension point. Model the stored shape as a
  plain record (nullable-style fields, not `option`) and map at the F#
  boundary — `Option.toObj`/`Option.ofObj`/`Option.toNullable`/
  `Option.ofNullable` (all in `FSharp.Core` already) usually make that a
  one-liner per field.

## What the kernel deliberately is NOT

No ORM, no query DSL, no workflow/BPMN engine, no projection generation, no event
sourcing, no LLM calls inside the kernel (deterministic infrastructure). If your
solution wants to "add one of these to the kernel": wrong turn — the answer is
almost certainly in an ADR or a concepts section.
