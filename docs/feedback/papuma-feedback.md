# Papuma.Kernel feedback

Friction, gaps and suggestions for Papuma.Kernel found while building applications on it —
jejak (F-1 to F-15) and aksara (F-16 onward) — input for the kernel's maintainer, not
application decisions. Newest first within each section.

Each entry names the **kernel version**, how it was found (**docs**, **API**, **code**),
whether it is **confirmed** or an **assumption**, and a concrete suggestion. When an entry
is addressed in the kernel, move it to *Resolved* with the version that fixed it.

## Open

aksara (F# wiki, 18 feed handlers, 31 own migrations) moved from 1.3.0 to 2.0.0 on
2026-09-27. Every entry below was checked against the 2.0.0 source (`src/`) and the
shipped docs, not only against aksara's code. Rough priority: F-16 and F-17 remove the
most consumer code; F-18 and F-19 are small; F-20 and F-21 are docs or convenience.

### F-21 — Scoped reads outside handlers take four lines per query site

- Found: 2026-09-27 · 2.0.0 · code · **confirmed**
- The tools are complete (`SetScopeAsync(tx, scope)`, `papuma.scope_visible` /
  `scope_writable`, recipe `same-database-read-models.md` §3). The friction is volume:
  aksara has about 60 read sites over its own `aksara.read_*` tables in endpoints and
  query slices, each today a plain `where workspace_id = @ws`. Moving them under RLS means
  connection + `BeginTransactionAsync` + `SetScopeAsync` + `cmd.Transaction` at every
  site — easy to get wrong once (a command without `Transaction` runs outside the scoped
  transaction) and the reason aksara has not adopted it yet.
- Suggestion: a helper that makes the safe form the short form, e.g.
  `await using var scoped = await dataSource.OpenScopedAsync(scope, ct)` returning the
  connection with an open, scope-set transaction and a `CreateCommand()` that already
  carries it; commit or roll back on dispose (read-only use: roll back). The recipe's §3
  would shrink to that call.

### F-20 — Append-only projections: the idempotency pattern is named, not shown

- Found: 2026-09-27 · 2.0.0 · code + docs · **confirmed**
- concepts §2 names the idempotency key (`handler + seq`), and upsert projections get it
  for free through a version guard. An append-only projection (aksara's
  `workspace-activity`: one row per page change, the newest 300 kept) has neither: an
  at-least-once redelivery or a checkpoint reset duplicates rows. For the 2.0.0 rebuild
  aksara had to empty the table before resetting the checkpoint (a one-shot
  `ISchemaContributor`, see F-16).
- Suggestion (docs only): a short paragraph in `projection-schema.md` or concepts §2 —
  store `seq` in a unique column, `INSERT … ON CONFLICT (seq) DO NOTHING`; note that a
  rebuild may order concurrent transactions differently (ADR-022), so ordering columns
  should come from the change (`occurred_at`, version), not from the insert order.

### F-19 — Unique keys cannot be conditional

- Found: 2026-09-13 (aksara ADR-0006 amendment) · 1.2.1, still in 2.0.0 · API ·
  **confirmed** (`DocumentTypeBuilder<T>.UniqueKey(Expression<…>)` is the only overload)
- aksara wanted "at most one `Membership` with `Role = "owner"` per workspace, any number
  of editors". A unique key constrains every value, so it cannot express that; the owner
  moved to a field on `Workspace`. That model is fine — arguably better — so this is low
  priority, but "unique among the documents in state X" is a common invariant (one
  active subscription, one default address, one open draft).
- Suggestion: an optional filter on keys, restricted to what translates into the partial
  index predicate — equality with a constant:
  `UniqueKey(x => x.UserId, where: x => x.Role == "owner")`.

### F-18 — A session disposed with uncommitted writes is silent

- Found: 2026-09-13 · 1.2.1, still in 2.0.0 · code · **confirmed**
  (`DocumentSession.DisposeAsync`: implicit rollback, no log)
- aksara's `InvitationStore.accept` saved the membership through `trySaveAsync`, got
  `Ok`, and never called `CommitAsync`. The write vanished; only a slice test noticed.
  The docs warn about it (AGENTS snippet, F# README), and aksara's own rules quote the
  warning — it still happened, because `Ok` reads as done.
- Suggestion: log a warning (with an `EventId`, document type and id) when a session with
  pending writes is disposed without commit or explicit rollback; optionally a strict
  mode that throws, for development and tests. In the F# facade, a
  `runSessionCommitted` variant that commits when the body returns `Ok` would make the
  common case impossible to get wrong.

### F-17 — Every projection reloads the document; changes arrive one at a time

- Found: 2026-09-27 · 2.0.0 · code · shape **confirmed**, cost **assumption — unmeasured**
- `ChangeRecord` carries the diff only (ADR-004), so a projection that needs more than
  the changed fields loads the document: 15 of aksara's 18 handlers open a session per
  change (`OpenSession(change.Scope)` → `LoadAsync`) and then write one row. A rebuild
  therefore costs changes × handlers sessions, reads and round trips.
- `IChangeHandler.HandleAsync` takes one change, so the batching that
  `same-database-read-models.md` itself recommends for high-volume projections ("group
  consecutive changes of the same scope into one transaction") cannot be done through
  the handler interface.
- Suggestions, cheapest first:
  1. An optional `IBatchChangeHandler.HandleBatchAsync(IReadOnlyList<ChangeRecord>, ct)`
     — the processor already reads batches; delivering them lets a projection group by
     scope, bulk-load and write in one transaction. Checkpoint semantics unchanged (the
     batch succeeds or is retried as a whole).
  2. `LoadManyAsync<T>(ids)` so a batch needs one read, not one per change.
  3. Further out: an opt-in, policy-masked post-image on the record (ADR-016's masked
     reads already exist) for handlers that only project fields — only if (1)+(2) turn
     out insufficient, since it contradicts diff-only storage unless computed at read.

### F-16 — The kernel cannot tell a projection from an effect handler; rebuilds are manual

- Found: 2026-09-27 · 2.0.0 · API + code · **confirmed** (`IChangeHandler` has `Name` and
  `HandleAsync` only; resetting is `ResetCheckpointAsync(name)` per handler)
- The 2.0.0 upgrade note says "reset every handler whose output must be complete". The
  kernel does not know which handlers those are: resettable projections and
  non-resettable effect handlers (aksara: LLM job markers) share one interface. aksara
  keeps the distinction in a table in its STATUS.md and, since this upgrade, in a
  hand-written list, and had to build its own one-shot rebuild: a migration inserts a
  request row, an `ISchemaContributor` (thanks to F-11 it runs before the workers) resets
  the listed checkpoints under a row lock and marks the request done.
- The recipe's route for breaking projection changes — a new handler name — leaves the
  old checkpoint and failure rows behind and fights the "`Name` is identity, never rename
  it" rule.
- Suggestions:
  1. A marker (`IProjection : IChangeHandler`, or `bool IsResettable`) so the kernel and
     its tools (dashboard, MCP) can show and act on the distinction.
  2. A `Version` on projections, stored with the checkpoint: when it differs at startup,
     the kernel resets that checkpoint once (before the workers start). A breaking
     projection change becomes "bump the version", the name stays stable.
  3. `ResetProjectionsAsync()` — every resettable handler of both feeds in one call, so
     an upgrade note like 2.0.0's becomes one line (or an option that runs it once).

## What works well

Kept so the maintainer knows what not to lose.

- **Agent-facing docs** (playbook, decision tree, hard rules, "what the kernel is NOT")
  shaped several jejak decisions directly — e.g. "human-in-the-loop = write a task
  document" became ADR-0006. The kernel ADRs shipped with the package explain *why*,
  which made the rules understandable rather than just followable.
- **Scope + RLS, `expectedVersion`, `ActorId`/`CausationType`** give jejak its atomic
  claim and its provenance essentially for free.
- **Deliberately narrow primitives** (no conditional patches, no query DSL) forced better
  designs — the id sets of ADR-0008 are better than what would have been built on a
  richer patch catalog.
- **How feedback was taken up (1.3.0)**: every entry resolved within a day, each with a
  kernel ADR or a documented reason, several deliberately narrower than suggested (the
  testing package, `GetDocument` instead of a diff accessor) with the reason stated. The
  XML docs made the verification fast — the reflection needed for 1.2.1 was unnecessary.
- **History and rollback for free** (aksara): page history is the change feed mapped to
  a DTO, rollback an ordinary update (ADR-008) — no audit table, no history model.
- **New handlers backfill themselves** (aksara): a retrieval projection added months
  after the first pages chunked and embedded every existing page on first boot — no
  backfill script, confirmed on the real development database.
- **The 2.0.0 upgrade was source-compatible** (aksara): no call site changed; the only
  work was the rebuild the upgrade note asked for (F-16) and an audit of the handlers
  against the new ordering — which the playbook's new rule made quick.
- **Deliberate "no" with reasons** (aksara): the fixed JSON configuration (no DU or
  `option` fields, concepts §29) costs an F# consumer a mapping layer, but the reason is
  stated and holds; not an open request.

## Resolved

F-10 to F-14 were checked against the 1.4.0 package on 2026-09-27 — `ISchemaContributor`
and `AddSchemaContributor<T>()` with the `projection-schema.md` recipe,
`GetChangesByCorrelationAsync` (XML docs), concepts §14 (measurement, document size,
partitioning trigger, tenant distribution), the integration-event guidance in §21, the
playbook and getting-started, and the evidence log in kernel ADR-021. jejak moved the same
day; the correlation read is in use in a slice test (see jejak's `docs/concepts.md`,
"Upgrade to Papuma.Kernel 1.4.0").


Resolved in 2.0.0 (2026-09-27) — to be checked against the package by jejak.

### F-15 — The change feed silently loses changes of interleaved transactions (critical)

- Found: 2026-09-27 · 1.4.0 · code · **confirmed, deterministic reproduction**
- **Symptom.** jejak's work-item list projection intermittently missed newly created
  items: the handler's checkpoint stood beyond the item's change, `papuma.failure` was
  empty, and the handler had never been called for that change. A delivery log around the
  handler showed the gap directly — from one failing run:

  | seq | txid | delivered |
  |---|---|---|
  | 96 | 997 | yes |
  | 97 | 1003 | **never** |
  | 98 | 997 | yes |
  | 99 | 1003 | **never** |
  | 100 | 997 | yes — checkpoint moves to 100 |

- **Mechanism.** Transaction 997 took its id first and wrote several changes over time;
  transaction 1003 (a newer id) wrote in between. 997 commits while 1003 is still open, so
  the horizon is `xmin = 1003`: `txid < xmin` delivers 96, 98 and 100, and the checkpoint
  moves to 100. When 1003 commits, 97 and 99 lie *behind* the checkpoint and are never
  read. The argument in concepts §2 — "as long as the slow writer is open, the faster 101
  is held back" — holds for writers with a **newer** transaction id only. A transaction
  with an **older** id that writes again after a newer one has drawn a sequence number
  passes the horizon test and pulls the checkpoint over the newer one's rows.
- **When it happens.** Any two concurrent sessions where the one that wrote first writes
  again after the other has written, and commits first. Multi-write sessions are the
  normal case (jejak: project counter + item + parent in one session), so this is not an
  edge case under concurrency. Every handler is affected — projections miss rows, and
  effect handlers (mails, bridges) silently skip their work.
- **Reproduction** (deterministic, 3/3 runs): `tests/Jejak.Core.Tests/Infrastructure/FeedGapReproduction.cs`,
  run with `dotnet test --project tests/Jejak.Core.Tests -- --explicit only`. Session A
  writes, session B writes, A writes again and commits; the processor delivers A's changes
  while B is open; B commits; B's change is never delivered.
- **Suggestion** (a direction, not verified against the kernel's code): move the cursor
  from a sequence number to a **transaction snapshot**, as PgQ does — store the snapshot of
  the last read (`pg_current_snapshot()`), and read the next batch as the changes whose
  `txid` is visible in the new snapshot but not in the stored one
  (`pg_visible_in_snapshot`), ordered by `seq` within the batch. A transaction's rows then
  become deliverable all at once, when it commits, and nothing can appear behind the
  cursor. Per-document order should survive, because two transactions writing the same
  document serialize on its row lock, so the later one's changes land in the same or a
  later batch with higher sequence numbers — worth a test.
- **Impact on jejak until fixed**: the claim tests that read `next` from the projection
  are occasionally red (a created item missing from the list); production would show the
  same as items missing from lists and the inbox. No workaround in jejak — the fix belongs
  in the feed.
- **Verified in jejak (2026-09-27)**: `FeedGapReproduction` passes as a regular test; six
  full suite runs in a row green (1.4.0: about one run in three failed); the development
  projection rebuilt through `ResetCheckpointAsync` with lag 0 afterwards; the test drain
  now waits for lag = 0 instead of a `seq` watermark.
- **Resolved in 2.0.0 (2026-09-27), as suggested — the PgQ snapshot cursor** (kernel
  ADR-022). A handler's position is now a transaction snapshot; each cycle delivers the
  transactions committed since, as a slice in `seq` order. Reproduced in the kernel
  (`FeedGapTests`, change and event feed) and covered by a concurrency stress test —
  interleaved multi-write sessions, random commit and rollback, two competing
  processors — that fails on the 1.4 engine every run. Per-document order holds as
  predicted (row lock). The event feed had the same defect and the same fix.
- **What changes for jejak:**
  - **Rebuild every projection after upgrading** (`ResetCheckpointAsync` per handler).
    2.0.0 stops new losses; rows skipped under 1.x are not replayed on their own. Effect
    handlers may have missed work — the records are intact in `papuma.change` /
    `papuma.event` if something must be caught up.
  - **Ordering is causal, not global `seq`**: a transaction after every one that
    committed before it began, concurrent transactions unordered, per document strictly
    by version. A lower `seq` can arrive after a higher one — never use `seq` as a
    watermark, and do not decide in a handler from the order of unrelated documents
    (live and rebuild may order concurrent transactions differently).
  - **Lag is a count** of committed, undelivered records; an open transaction is no lag
    and no longer stalls the feed — only its own rows wait.
  - Startup adds `txid` indexes and checkpoint columns (existing checkpoints carry over).
  - `FeedGapReproduction.cs` should now pass; worth keeping as a regression test.

Resolved in 1.4.0 (2026-09-27) — to be checked against the package by jejak. 1.4.0 also
fixes retry timing on PostgreSQL (the due check now uses the database clock, not the
application's), which affects jejak's handlers without any change on jejak's side.

### F-14 — Write-path scaling: measure, then optimise without changing the model

- Found: 2026-09-26 · 1.3.0 · design review · **assumption — unmeasured**
- Context: the question was whether document-as-truth scales under a massive increase in
  writes. The fair comparison is not plain CRUD but CRUD *plus* an audit table and an
  outbox — which also costs an update plus an insert per write; the kernel does both in
  one statement. The hard limits under heavy write load (one consumer per handler, one
  global order, one primary) are not specific to the document model and would equally
  apply to event sourcing on PostgreSQL. Two costs *are* specific to the implementation,
  not to the idea:
  1. **HOT updates are probably never possible on `papuma.document`.** PostgreSQL can
     only update a row without touching its indexes when no indexed column changes. The
     declared unique and lookup keys are expression indexes over `data`, and `data`
     changes on every write — so every save likely inserts new entries into every key
     index whose partial predicate matches, even when no key value changed. Check with
     `n_tup_hot_upd` vs `n_tup_upd` in `pg_stat_user_tables` for `papuma.document`.
  2. **Large documents are rewritten whole.** A relational row keeps unchanged large
     columns in TOAST; a single JSONB column is rewritten and re-toasted on every change,
     so cost grows with document size, not change size.
- Suggestions, in order:
  1. **A benchmark with realistic shape** before changing anything: documents of 5–50 KB,
     several declared keys, 16–32 concurrent sessions; measure saves/s, latency, WAL bytes
     per save and the HOT ratio. It decides whether the points above are real.
  2. **If HOT is indeed lost: move keys to a side table**, e.g.
     `papuma.document_key(tenant_id, document_type, key_name, value, document_id)` with the
     unique constraint there, maintained in the same statement and written only when a
     key value actually changes. `papuma.document` would keep only its primary key, and
     ordinary updates could be HOT. Together with a `fillfactor` below 100 on
     `papuma.document`, so HOT finds room on the page.
  3. **Partition `papuma.change` by time**, with BRIN indexes on `seq` / `occurred_at`:
     the history is never deleted, so it grows without bound; partitions keep indexes and
     vacuum bounded and allow archiving without giving up "never delete".
  4. **Document size as a modelling cost** in the docs, next to granularity; mention LZ4
     TOAST compression (`default_toast_compression = lz4`) for larger documents.
  5. The feed-side escape routes already planned in concepts §14 (parallel handlers per
     cycle, a SQL-side type filter, sharding by document id) address the first wall and
     need nothing from the document model.
  6. Beyond one primary: `tenant_id` in every key and policy makes tenant-based
     distribution (e.g. Citus) a natural fit — worth a paragraph in concepts §14 as the
     long-range escape route.
- **Resolved in 1.4.0 — measured, redesign not needed now.** A write-path probe
  (`benchmarks/Papuma.Kernel.Benchmarks`, `writepath`) confirmed point 1: any declared
  key takes HOT from 92–100 % to 0 %. At realistic shape (5–50 KB incompressible
  documents, 16–32 sessions) that costs 0–9 % WAL and no measurable throughput, so the
  trigger for the key side table did not fire (resolution limit of a local container:
  ~25 %). concepts §14 now covers document size as a modelling cost (a save rewrites
  the whole document; `default_toast_compression = lz4`), defers change-table
  partitioning with a trigger (~100 M rows or autovacuum as bottleneck) and names
  tenant-based distribution as the long-range route. For jejak: keep frequently patched
  counters in small documents of their own.

### F-13 — Guide consumers to keep the raw feed inside the application

- Found: 2026-09-26 · 1.3.0 · docs · assumption (design review, see
  jejak's `docs/papuma-review.md`)
- A change record is a diff over the document's field paths, so every consumer couples to
  the storage shape. Inside one application that is fine; across team or system
  boundaries the internal model becomes an integration contract. The pieces for doing it
  right exist (events for facts, translation slices, the NATS bridge), but concepts §21
  presents the raw feed as a cross-language API, which invites the coupling.
- Suggestion: a short section in getting-started or the playbook — "the feed is for your
  own projections and reactions; publish explicit integration events at the boundary" —
  and a caveat in §21.
- **Resolved in 1.4.0**: concepts §21, the feed wire format, the playbook,
  getting-started and both factsheets separate the stable *format* from the *content*
  (the documents' field paths) and send other teams and systems to explicit integration
  events (event log facts or translation slices, carried by a bridge handler).

### F-12 — No way to read a command's changes as one unit

- Found: 2026-09-26 · 1.3.0 · docs · confirmed (kernel ADR-021 lists it as a prerequisite)
- One jejak command writes several documents (create a child: project counter, new item,
  parent). The change records share a correlation id, but there is no read API by
  correlation id — neither for a timeline entry ("what did this command do?") nor for
  tests asserting a command's complete effect.
- Suggestion: `GetChangesByCorrelationAsync(correlationId)` (scope-bound like every read).
- **Resolved in 1.4.0**: `session.GetChangesByCorrelationAsync(correlationId)` — every
  change of a unit of work, across document types, in feed order, scope-bound, including
  the session's own uncommitted writes; also as the read-only MCP tool
  `get_changes_by_correlation`. Backed by a new index on `papuma.change`: on a large
  change table create it `CONCURRENTLY` before upgrading (CHANGELOG 1.4.0, upgrade notes).

### F-11 — No migration story for projection tables

- Found: 2026-09-26 · 1.3.0 · docs · confirmed (gap in docs)
- The kernel manages its own schema idempotently and, since 1.3.0, documents RLS for
  application tables — but projection tables still need creating and evolving, and every
  adopter solves that on day two. jejak is about to.
- Suggestion: a recipe with a plain migration approach (idempotent DDL at startup next to
  `EnsureSchemaAsync`, or a named tool), including the RLS policies and
  `GrantAppRoleAsync` in tests.
- **Resolved in 1.4.0, as a hook plus a recipe.** Projection DDL has to run after the
  kernel schema (its RLS policies call the ADR-019 functions) and before the feed
  workers (handlers write into the tables) — which no application hosted service could
  guarantee. `AddSchemaContributor<T>()` with an `ISchemaContributor` runs exactly there.
  Recipe `recipes/projection-schema.md`: idempotent DDL, `pg_advisory_xact_lock` for
  concurrent starts, breaking changes as a rebuild through a new handler name. For
  jejak: move the projection tables' DDL into a contributor.

### F-10 — Data point for the testing-package revisit (ADR-021)

- Found: 2026-09-26 · 1.3.0 · code · confirmed (one consumer)
- Kernel ADR-021 defers framework adapters until two consumer repositories carry the same
  helper. jejak is one: `tests/Jejak.Core.Tests/Infrastructure/PostgresFixture.cs` wraps
  `PapumaTestDatabase` in an xUnit v3 **assembly fixture** (start once per run, create the
  store for the model, dispose) — about fifteen lines, none of them jejak-specific.
- Not a request yet; recorded so the revisit has its first piece of evidence. The
  domain-specific helper (`WorkspaceInvariants`) is *not* a candidate — it checks jejak's
  own invariant.
- **Recorded in 1.4.0**: ADR-021 has an evidence log; jejak's assembly fixture is data
  point 1 of 2 for an xUnit adapter. The correlation read it listed as a prerequisite
  for change assertions now exists (F-12).

All nine were checked against the 1.3.0 package on 2026-09-26 (XML docs, shipped docs,
link check over `docs/`, API) and jejak moved to the new APIs the same day — see
jejak's `docs/concepts.md`, "Upgrade to Papuma.Kernel 1.3.0". Confirmed in jejak's running
system: the atomic key counter leaves no gap after a refused command (F-4), and GUID tenant
ids are stored in the canonical dashed form (F-8).

### F-9 — Using the store without the host is undocumented

- Found: 2026-09-25 · 1.2.1 · code · confirmed
- The docs show only `AddPapumaKernel` in a host. Tests (and tools) need the store
  without one; the way to do it — `SchemaManager.EnsureSchemaAsync(dataSource, model, ct)`
  and `new DocumentStore(dataSource, model)` — had to be found by reflection, since the
  package has no XML docs (F-1).
- Suggestion: a short "without a host" section in getting-started, ideally with the test
  setup (see F-7).
- **Resolved in 1.3.0.** getting-started §7 "Without a host": the hand-built store
  (`SchemaManager.EnsureSchemaAsync` + `new DocumentStore`) and running handlers via
  `ChangeFeedProcessor.ProcessOnceAsync()`; the test setup went into
  `Papuma.Kernel.Testing` (F-7). XML docs ship with the package (F-1).

### F-8 — Tenant id format is enforced but not documented

- Found: 2026-09-25 · 1.2.1 · code · confirmed
- `ScopeContext.Tenant(id)` throws unless the id matches `[A-Za-z][A-Za-z0-9_]{1,100}`.
  Nothing in the shipped docs mentions it; a UUID — the obvious choice for a tenant id —
  fails at runtime. jejak uses `"w" + uuid.ToString("N")`.
- The rule excludes `-`, the character every standard UUID string contains, and requires
  a leading letter, which excludes UUIDs in any format. Consumers work around it with
  prefixes and dash-free formatting — every one of them slightly differently. The reason
  for the rule is not visible from outside (assumption: the id ends up somewhere
  identifier-like — a channel name, a setting — where `-` would need quoting).
- Suggestions, in order of preference:
  1. **Allow UUIDs directly**: extend the pattern to accept `-` and a leading digit
     (e.g. `[A-Za-z0-9][A-Za-z0-9_-]{0,100}`), quoting or mapping the id wherever it is
     used as an identifier.
  2. If the restriction has to stay: a typed overload `ScopeContext.Tenant(Guid id)` that
     derives the valid form in one canonical way, so all consumers store the same shape.
  3. At minimum: document the rule and its reason where `ScopeContext.Tenant` is
     introduced, and name the pattern in the exception's documentation.
- **Resolved in 1.3.0** — suggestions 1 and 2 plus 3. The pattern is now
  `^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$`, so GUID strings fit; `ScopeContext.Tenant(Guid)`
  yields the canonical lowercase dashed form (ids compare case-sensitively). The reason
  is documented: a whitelist only — tenant ids reach SQL exclusively as parameters.
  Every previously valid id stays valid, so jejak's `"w" + uuid.ToString("N")` keeps
  working; switching stored tenants to the GUID form would be a data migration.

### F-7 — No testing support package

- Found: 2026-09-25 · 1.2.1 · docs · assumption (no code yet)
- Every consumer rebuilds the same pieces: a Testcontainers fixture with `postgres:18`,
  per-test tenant isolation, schema setup, and GIVEN/WHEN/THEN helpers that assert the
  resulting documents and change records. The event-modeling recipe says the kernel
  tests itself this way, so the pieces likely exist internally.
- Suggestion: a small `Papuma.Kernel.Testing` package — fixture, fresh-tenant helper,
  assertion on the expected `ChangeRecord` for a command.
- **Resolved in 1.3.0, deliberately narrower than suggested** (kernel ADR-021).
  `Papuma.Kernel.Testing`: `PapumaTestDatabase` (Testcontainers or an existing server,
  with a non-superuser role so tests exercise RLS — a superuser connection passes even
  when isolation is broken) and `DrainAsync()` on the feed processors (throws on handler
  failures instead of passing over them). Test-framework agnostic. No fresh-tenant
  helper (`ScopeContext.Tenant(Guid.NewGuid())` is one) and no GIVEN/WHEN/THEN DSL yet:
  deferred until two consumer repos carry the same helper — jejak's own helpers are
  exactly the evidence that trigger needs, worth reporting here.

### F-6 — No recipe for tenant isolation of read tables in the same database

- Found: 2026-09-25 · 1.2.1 · docs · confirmed (gap in docs)
- `recipes/external-read-models.md` covers isolation for external targets well (tenant
  in the key, mandatory filter). The most common case — projection tables in the *same*
  PostgreSQL database — has no guidance. jejak's projections (ADR-0007) sit outside
  Papuma's RLS and must re-establish isolation by hand.
- Partly answered (concepts §23): the kernel sets **transaction-local** GUCs for its own
  RLS. Application tables could only reuse them inside the kernel's transaction, which a
  projection handler does not share. Whether that is intended to be reusable is open.
- Suggestion: a recipe "read tables in the same database" with an RLS policy template —
  or, if the context exists, document it as a supported extension point.
- **Resolved in 1.3.0** (kernel ADR-019). The schema provides
  `papuma.scope_visible(scope, tenant_id)` / `papuma.scope_writable(scope, tenant_id)`
  as the supported contract (the setting names stay internal). Policy:
  `USING (papuma.scope_visible(scope, tenant_id)) WITH CHECK (papuma.scope_writable(scope, tenant_id))`
  with `ENABLE` + `FORCE ROW LEVEL SECURITY`; the projection handler opens its own
  transaction and calls `SetScopeAsync(tx, change.Scope)` per change (not the `All`
  scope, which reads but never writes). Recipe: `recipes/same-database-read-models.md`,
  verified end to end in the kernel's test suite.

### F-5 — Composite unique keys not documented

- Found: 2026-09-25 · 1.2.1 · docs · confirmed (not documented); support unknown
- Kernel ADR-006 shows single-field keys only (`UniqueKey(x => x.Email)`). jejak needs
  "number unique per project" and flattened it into a single string field `Key`
  (`JEJ-42`) — workable, but the natural model is `(ProjectId, Number)`.
- Suggestion: either support composite keys (expression index over several fields) or
  document the limit and the flattening workaround.
- **Resolved in 1.3.0** (kernel ADR-020): composite keys,
  `UniqueKey(x => new { x.ProjectId, x.Number })`, one multi-expression partial index;
  lookup with `LoadByKeyAsync<T>(x => new { x.ProjectId, x.Number }, ["p1", 42])`.
  Enforced only when every component is present, like single-field keys. Moving jejak
  from the flattened `Key` field leaves that field's old index in place — the kernel
  never drops indexes, so drop it by hand.

### F-4 — `PatchAsync` does not expose the new value

- Found: 2026-09-25 · 1.2.1 · API (reflection) · confirmed
- `PatchAsync` returns `SaveResult { Version, Operation, Diff }`. The value produced by
  `Increment` is only reachable by reading the diff of a tracked field. Sequential
  numbers (ticket keys, invoice numbers) are a standard case; jejak falls back to load +
  save with `expectedVersion`, which has a conflict window `Increment` would avoid.
  `RETURNING new.data` already has the value.
- **Confirmed in practice** (2026-09-25, code): five concurrent item creations in one
  project exhausted three load-and-save attempts on the project counter; jejak now needs
  up to ten attempts with jitter for what one atomic `Increment` returning its value would
  do in a single statement.
- Suggestion: a typed accessor (e.g. `result.Diff.NewValue(x => x.Counter)`) or a
  documented "sequence numbers" recipe.
- **Resolved in 1.3.0**: `SaveResult.GetDocument<T>()` returns the persisted state —
  `(await session.PatchAsync<Project>(id, p => p.Increment(x => x.NextNumber))).GetDocument<Project>().NextNumber`
  replaces the load + save retry loop. Taken instead of a `Diff` accessor because the
  diff is policy-applied and holds only changed fields. concepts §17 gained a "sequence
  numbers" paragraph (the increment rolls back with the session; a retried command
  still needs idempotency, F-3).

### F-3 — `Increment` is not idempotent under retried commands; docs don't say so

- Found: 2026-09-25 · 1.2.1 · docs · confirmed
- concepts §17 recommends `Increment` + validator for bounded counters — correctly safe
  against concurrency. Not mentioned: a command retried after a timeout (typical for
  agents calling through MCP, or any client retry) applies the increment twice. jejak's
  first design (ADR-0007) used counters for readiness and had to be amended (ADR-0008,
  id sets) because of exactly this.
- Suggestion: one sentence in §17 and the playbook — "`Increment` is safe against
  concurrency, not against duplicate commands; make the command idempotent or use set
  semantics".
- **Resolved in 1.3.0**: concepts §17 and the playbook's hard rule state it —
  safe against concurrency, not against duplicate commands; idempotent command or
  set semantics.

### F-2 — Shipped docs drift from consumer copies; dead links in the package

- Found: 2026-09-25 · 1.2.1 · docs · confirmed
- The playbook copied into jejak (`docs/ai/`) already differs from the one in 1.2.1
  (`../getting-started.md` vs. `../vNEXT/getting-started.md`, `legacy/…` vs. `vNEXT/…`).
  Other consumer projects presumably carry other versions.
- Inside the 1.2.1 package, the playbook links to two files that are not shipped:
  `../analyses/local-kernel-sqlite-sibling.md` and `../vNEXT/implementation-plan.md`.
- The docs of a released 1.2.1 package live in a folder named `vNEXT`, which reads as
  "not this version".
- Suggestions: have the AGENTS snippet point agents at the package's own docs
  (`~/.nuget/packages/papuma.kernel/<version>/docs/`) instead of inviting copies; a link
  check in the package build; a version-neutral docs folder name. Further out:
  `Papuma.Kernel.Mcp` serving the docs as MCP resources, so an agent reads the docs of
  exactly the version it builds against.
- **Resolved in 1.3.0**: `vNEXT` is gone; every package ships the same `docs/` set;
  links outside it are absolute; a test fails CI on any link or anchor that would
  dangle inside a package; the AGENTS snippet points agents at
  `~/.nuget/packages/<package>/<version>/docs/` instead of copies — jejak's
  `docs/ai/` copy can be replaced by that pointer. Not done: serving the docs as MCP
  resources.

### F-1 — No XML documentation in the package

- Found: 2026-09-25 · 1.2.1 · API · confirmed
- `lib/net10.0/` contains only the DLLs, no `.xml`. No IntelliSense, and agents cannot
  see signatures or remarks — whether `PatchAsync` returns the new value had to be
  answered by reflection.
- Suggestion: `<GenerateDocumentationFile>true</GenerateDocumentationFile>` for the
  packed projects.
- **Resolved in 1.3.0**: every packed project generates XML documentation; the
  `.xml` ships next to each DLL.
