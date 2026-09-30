# Papuma.Kernel — Playbook for AI Agents

Status: verified against the implemented API (phase 13, 2026-06-12) ·
Audience: coding agents that **build applications using Papuma.Kernel** (this
page is Postgres-framed by default; the SQLite sibling's differences are
called out separately below).

This document is the entry map. The truth lives in the reference chain:
[getting-started.md](../getting-started.md) (the API in 5 minutes) →
[concepts.md](../concepts.md) (the why, §1–§32) →
[ADRs](../adr/) (binding decisions) →
[architecture.md](../architecture.md) (data model, namespaces).

## The mental model in four sentences

1. **The JSON document is the truth.** No event sourcing: state is stored
   directly (`papuma.document`), and the change feed is *derived* as a reversible
   field diff — atomically in the same statement (PostgreSQL ≥ 18,
   `RETURNING OLD/NEW`). On `Papuma.Kernel.Local` (SQLite) the same guarantee
   holds via a different mechanism — see "Differences" below.
2. **The session is a unit of work.** All writes of a session commit atomically
   (`CommitAsync`) or not at all (dispose without commit = rollback, logged as a
   warning; `DiscardAsync` = deliberate rollback, silent).
3. **Reacting happens via feeds.** Change and event handlers consume in causal
   order (per document strictly by version; concurrent transactions unordered),
   checkpointed, at-least-once. Never treat `seq` as a watermark ("skip ≤ highest
   seen") — a lower `seq` can arrive later. Never decide in a handler from the order
   of unrelated documents (first-come-first-served, "latest across documents") — a
   rebuild may order them differently; put such decisions in the write path (bounded
   counter, unique key, `expectedVersion`). A projection implements `IProjection`
   (`Version` + idempotent `ResetAsync`, `TRUNCATE`); an effect handler gets
   `[StartsAtFeedHead]`. The kernel generates no projections —
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
    .Document<Ticket>(d => d.UniqueKey(x => new { x.ProjectId, x.Number }))   // composite (ADR-020)
    .Event<UserLoggedIn>(e => e.Retention(TimeSpan.FromDays(90)))); })
  .AddSchemaContributor<ReadModelSchema>()   // projection tables: after kernel schema, before workers
  .AddChangeHandler<UserProjection>()
  .AddEventHandler<LoginAudit>();

// Writing (session = UoW; scope is mandatory)
await using var s = store.OpenSession(ScopeContext.Tenant("acme"),   // or Tenant(guid)
    new SessionOptions { ActorId = "harry", CausationId = "cmd-42", CausationType = "PlaceOrder" });
await s.SaveAsync(doc, expectedVersion);            // 0 = insert
var patched = await s.PatchAsync<User>(id, p => p.Set(x => x.Name, "H").Increment(x => x.LoginCount));
patched.GetDocument<User>().LoginCount;             // the persisted state, no reload
await s.PatchWhereAsync<User>(x => x.Status, "old", p => p.Set(x => x.Status, "new"));
await s.DeleteAsync<User>(id, expectedVersion);
await s.RollbackAsync<User>(id, toVersion, expectedVersion);   // append-only
await s.AppendAsync(new UserLoggedIn(id, "web"));   // event, atomic with the writes
await s.CommitAsync();                              // otherwise: rollback on dispose

// Reading (strong consistency)
var r = await s.LoadAsync<User>(id);                // r.Document, r.Version
var byKey = await s.LoadByKeyAsync<User>(x => x.Email, "x@y.de");
var ticket = await s.LoadByKeyAsync<Ticket>(x => new { x.ProjectId, x.Number }, ["p1", 42]);
var many = await s.LoadManyAsync<User>(ids);        // one query; missing ids absent, by id
var history = await s.GetHistoryAsync<User>(id, fromVersion: 3);
var commandEffect = await s.GetChangesByCorrelationAsync(correlationId);   // one unit of work, all types

// Reacting — a projection declares itself (ADR-024); an effect gets [StartsAtFeedHead]
public sealed class UserProjection : IChangeHandler, IProjection
{
    public string Name => "user-projection";        // = checkpoint identity!
    public int Version => 1;                        // raise → rebuilt once at the next start
    public Task ResetAsync(CancellationToken ct) { … }   // idempotent, e.g. TRUNCATE
    public Task HandleAsync(ChangeRecord change, CancellationToken ct) { … }
}

// Diagnostics (phase 11) · GDPR (phase 12)
await processor.GetLagAsync(); await processor.GetFailuresAsync();
await processor.RetryFailureAsync(name, seq);
await processor.ResetProjectionsAsync();           // or ResetProjectionAsync(name) — projections only
DataInventory.Build(model);                         // Art. 30 + policy review

// Tests (Papuma.Kernel.Testing, ADR-021)
await using var db = await PapumaTestDatabase.StartAsync();   // non-superuser app role: RLS applies
var testStore = await db.CreateStoreAsync(model);
await processor.DrainAsync();                                  // throws if a handler failed
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
| …read several documents of one type by id (a projection's batch, a detail view) | `LoadManyAsync<T>(ids)` — one query instead of one per id |
| …**any derived read** — list, join, aggregation, search, external *(the default)* | an `IChangeHandler` projection (eventual, lag observable) |
| …create or change a projection table | an `ISchemaContributor` with idempotent DDL (`AddSchemaContributor<T>()`); breaking changes = raise the projection's `Version` (rebuilt once at the next start), or new table + new handler name for zero downtime — never an in-place data migration ([recipe](../recipes/projection-schema.md)) |
| …rebuild projections (bug fix, kernel upgrade note) | raise `Version`, or `ResetProjectionsAsync()` on the processor — effects are left alone; never `ResetCheckpointAsync` on an effect (ADR-024) |
| …add a handler with side effects (mail, webhook, bus) to a system with history | `[StartsAtFeedHead]` on the class — otherwise its first start acts on every past record (ADR-024) |
| …show or test what one command did | `GetChangesByCorrelationAsync(session.CorrelationId)` — every change of that unit of work, all document types, feed order |
| …keep tenant isolation on a projection table in the same database | RLS policy `USING (papuma.scope_visible(scope, tenant_id)) WITH CHECK (papuma.scope_writable(scope, tenant_id))` plus a restrictive `FOR DELETE USING (papuma.scope_writable(...))` policy (a DELETE never sees `WITH CHECK`), handler opens `appData.OpenScopedAsync(change.Scope)` per change (reads the same way, with the reader's scope) — never copy the GUC names ([recipe](../recipes/same-database-read-models.md), ADR-019) |
| …ad-hoc SQL/BI, all 4 conditions met *(the exception)* | view with `security_invoker = on` (concepts §16) |
| …change a single field without loading | `PatchAsync` (field-level LWW is deliberate there) |
| …bound a stock (never oversell) | `Increment(-1)` + `Validate` (concepts §17) |
| …hand out sequential numbers (ticket keys, invoices) | `PatchAsync(id, p => p.Increment(x => x.Next))`, then `result.GetDocument<T>().Next` — atomic, no reload; make the command idempotent, or a retry issues a second number (concepts §17) |
| …allow at most one document in a state per parent ("one owner per workspace", "one default address") | a slot document whose id is derived from the parent (`WorkspaceOwner` with `Id = workspaceId`), inserted with `expectedVersion: 0` **first** in the session — not a conditional key (concepts §34, ADR-020) |
| …enforce uniqueness over two fields ("number per project") | composite key `UniqueKey(x => new { x.ProjectId, x.Number })`; lookup `LoadByKeyAsync<T>(x => new { … }, ["p1", 42])` — no flattened helper field (ADR-020) |
| …use a GUID as tenant id | `ScopeContext.Tenant(guid)` — canonical lowercase dashed form; string ids match `^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$` and compare case-sensitively |
| …use the store in tools without a host | `SchemaManager.EnsureSchemaAsync(dataSource, model)` + `new DocumentStore(dataSource, model)`; handlers via `ChangeFeedProcessor.ProcessOnceAsync()` ([getting-started §7](../getting-started.md#7-without-a-host-tests-tools-console-apps)) |
| …write integration tests | `Papuma.Kernel.Testing`: `PapumaTestDatabase.StartAsync()`, `CreateStoreAsync(model)` (runs as a non-superuser role — RLS applies), `processor.DrainAsync()` (throws on handler failures); in a fixture that boots the real host, `o.RunFeedWorkers = false` so the drain does not race the hosted loops; fresh tenant per test, unique handler and type names ([getting-started §7](../getting-started.md#integration-tests-papumakerneltesting), ADR-021) |
| …let another team or system react to changes | explicit integration events at the edge — event log facts or a translation slice, carried by a bridge handler (NATS/webhook); the raw feed is for the owning application's own projections and reactions (concepts §21) |
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
  `RetryFailureAsync(handler, seq)`; if the checkpoint already passed it, and it is
  a projection: `ResetProjectionAsync(name)` rebuilds it; an effect is never reset —
repeat its lost work by hand.
- **`ConcurrencyException` piles up on one document**: a hot document. Check
  whether `PatchAsync` (independent fields) or `Increment` (counters) can replace
  the load-modify-save (concepts §4/§17).
- **Saves slow or WAL-heavy with large documents**: a save rewrites the whole
  JSONB document. Split frequently patched state into small documents, consider
  `default_toast_compression = lz4` (concepts §14, "The write path"). Do not drop
  declared keys to speed up saves: they switch off HOT updates, but at 5–50 KB
  documents that measured as 0–9 % more WAL and no throughput difference.
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
- **Feed wakeup is in-process** (`SqliteChangeNotifier`: every processor holds
  its own subscription, a commit wakes all of them), not `LISTEN`/`NOTIFY` — the
  realtime-UI-notifications recipe's *pattern* still applies, its
  Postgres-specific wiring doesn't.
- **Handlers run outside any feed transaction.** The processor reads a batch,
  releases the database, runs the handlers, then records the checkpoint. A local
  projection writes to the same `.db` file through its own connection; a slow
  handler does not block the application's writes.
- **One writer, one file, one process.** Don't open the same `.db` file from
  two processes — that's an OS file-lock violation, not a kernel concern to
  code around.
- **Schema contributors take a `SqliteConnection`.** `AddSchemaContributor<T>()`
  with an `ISqliteSchemaContributor` runs after the kernel schema, before the feed
  workers — same place as on Postgres, no advisory lock needed (one process owns
  the file). SQLite has no `ADD COLUMN IF NOT EXISTS`: check `pragma_table_info`.
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

- **`Patch` takes F# lambdas.** `p.Set((fun x -> x.Name), v)`,
  `p.Remove(fun x -> box x.Note)`, `p.Increment((fun x -> box x.Count), n)` — `box`
  where the parameter is `obj`. Annotate the parameter (`fun (x: Order) -> …`)
  unless a type argument fixes it — F# otherwise infers the most recently declared
  record type with that field name. `SetQ`/`RemoveQ`/`IncrementQ` (quotations) are
  deprecated; do not write new code with them.
- **Keys take `box` lambdas; composite keys a tuple.** `d.UniqueKey(fun x -> box x.Email)`,
`d.UniqueKey(fun x -> box (x.ProjectId, x.Number))` — not an anonymous record (F# sorts
its fields). For `LoadByKeyAsync` with a composite key, annotate the values as
`IReadOnlyList<obj>`, or F# picks the single-value overload.
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
  the sample shipped with before it was caught by actually running it. Use
  **`runSessionCommitted`** for a body that returns a `Result`: it commits on
  `Ok` and discards on `Error`, so `Ok` really means done.
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
