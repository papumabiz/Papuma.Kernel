# Changelog

## Unreleased

Changes:

- **Docs: "at most one per …"** (feedback F-19) — concepts §34 and the playbook show the
  slot document (an id derived from what it is unique for, inserted first with
  `expectedVersion: 0`) as the way to express uniqueness among documents in a state.
  Conditional keys are deferred against a trigger (ADR-020, amended).
- **Tooling: `publish-nuget.sh --tag vX.Y.Z`** packs the tagged commit in a temporary
  worktree instead of the working tree (which may be ahead of the tag — the docs ship
  inside the packages), and refuses to upload unless every package carries the tag's
  version. Without `--tag` it warns when HEAD is not a tagged commit or has uncommitted
  changes. The output path is passed to `dotnet` natively on Git for Windows.

## 2.1.0 (2026-09-28)

The feedback round from aksara (F-16 to F-21) and a row-level security fix. **Every
PostgreSQL user of 2.0.x should upgrade** — in 2.0.x the `All` scope could delete rows of
every tenant, and event retention deleted nothing under RLS. Source-compatible; the
schema migrates itself at startup. Add the `DELETE` policy below to your own RLS tables.

Upgrade notes — what a consumer of 2.0.x can notice:

- **A session disposed with uncommitted writes now logs a warning** (event id 1001,
  `UncommittedSessionDisposed`) and counts `papuma.session.uncommitted_disposals`. The
  rollback itself is unchanged. Where a rollback is intended, call `DiscardAsync()` first;
  a dispose on an exception path still logs — next to the exception.
- **Projections declare themselves** (`IProjection`, ADR-024) — optional, nothing changes
  for unmarked handlers. When you mark an *existing* projection, its first start only
  records the declared `Version`: it does **not** rebuild, or the upgrade would rebuild
  everything. To rebuild, raise the version or call `ResetProjectionsAsync()`.
- **The MCP tool `reset_feed_checkpoint` only resets projections now** — mark the
  handlers you reset through it with `IProjection`; it empties their target
  (`ResetAsync`) before the replay, and refuses effect handlers.
- **`papuma.checkpoint` gains `projection_version`** (both kernels), added at startup.
- **Add a `DELETE` policy to your own RLS tables** (PostgreSQL). The ADR-019 template —
  `USING (scope_visible) WITH CHECK (scope_writable)` — let the `All` scope delete every
  tenant's rows, because PostgreSQL checks a `DELETE` against `USING` only. Add to each
  table built from it:
  `CREATE POLICY scope_delete ON <table> AS RESTRICTIVE FOR DELETE USING (papuma.scope_writable(scope, tenant_id));`
  The kernel's own tables get it from `EnsureSchemaAsync`.

Changes:

- **New: a forgotten `CommitAsync` is no longer silent** (feedback F-18). Both kernels'
  sessions log and count a dispose with uncommitted writes, naming the correlation id,
  the number of writes and the first of them. `DocumentSession.DiscardAsync()` /
  `SqliteDocumentSession.DiscardAsync()` roll back on purpose and keep the session
  usable. The stores take an optional `ILogger` (new constructor overloads;
  `AddPapumaKernel` / `AddPapumaKernelLocal` pass it from DI). F#: `runSessionCommitted`
  commits when the body returns `Ok` and discards on `Error`. A throwing strict mode was
  left out on purpose: a dispose on an exception path would replace the original
  exception.
- **New: `NpgsqlDataSource.OpenScopedAsync(scope)`** (feedback F-21) — a connection with
  an open transaction whose scope is already set, for application tables under RLS
  (ADR-019). Its `CreateCommand()` binds every command to that transaction, so none runs
  outside the scope by accident; disposing without `CommitAsync` rolls back. Replaces
  the four-line open/begin/`SetScopeAsync`/`cmd.Transaction` sequence at every read and
  projection site; the same-database recipe uses it.
- **Docs: append-only projections** (feedback F-20) — `projection-schema.md` §3 keys the
  rows by the change's `seq` (`ON CONFLICT (seq) DO NOTHING`), so redelivery and rebuild
  add nothing twice and a reset needs no `TRUNCATE`; reads order by `seq`, which is the
  same live and after a rebuild; pruning to the newest N stays correct.
- **New: `LoadManyAsync<T>(ids)`** on both kernels' sessions (feedback F-17, part 2) —
  several documents of one type by id in one query, scope-bound, including the session's
  own uncommitted writes; missing ids are absent from the result, duplicates load once.
  The batch handler interface from the same entry is deferred until a measurement shows
  the per-change reload to be the bottleneck.
- **`Papuma.Kernel.Testing`: `GrantAppRoleAsync(schema)` also grants `TRUNCATE`** on
  application schemas — the reset of a projection under row-level security — but never
  on `papuma`, where `TRUNCATE` would bypass RLS.
- **New: projections and effects are declared** (ADR-024, feedback F-16). A projection
  implements `IProjection` — a `Version` and an idempotent `ResetAsync` that empties its
  target. A raised version rebuilds it once at the next start, under the same name;
  instances still running older code in a rolling deploy pause it instead of writing the
  old shape. `ResetProjectionsAsync()` rebuilds all projections of a processor,
  `ResetProjectionAsync(name)` one. `[StartsAtFeedHead]` makes an effect handler's first
  start skip the history. Lag snapshots carry `ProjectionVersion` and `Paused`; the
  dashboard and `get_feed_lag` show each handler's kind. Both kernels, both feeds.
- **Fixed (PostgreSQL, security): the `All` scope could delete** rows of every tenant in
  `papuma.document`, `papuma.change` and `papuma.event` (and in application tables built
  from the ADR-019 template): a `DELETE` is checked against the policy's `USING` only.
  A restrictive `DELETE` policy now requires a writable scope; `All` reads and never
  writes, as ADR-019 always stated (amended).
- **Fixed (PostgreSQL): event retention deleted nothing under row-level security.** The
  purge set no scope, so a role subject to RLS — the application login, even as table
  owner under `FORCE ROW LEVEL SECURITY` — saw no rows. It now finds the affected tenants
  under `All` and deletes under each tenant's scope. Its test ran as a superuser, which
  RLS does not filter; it now runs as a plain login role.
- **Fixed (PostgreSQL): the repair after a logical restore (2.0.0) failed under RLS** —
  its `UPDATE` ran under `All`, which `WITH CHECK` rejects, so `EnsureSchemaAsync` would
  have stopped the application's start after a `pg_restore`. It now writes tenant by
  tenant; tested as a plain login role.

## 2.0.0 (2026-09-27)

A major version for one reason: the feed's ordering contract changes (causal order
instead of global `seq` order) and direct-SQL consumers must switch queries. The API
is source-compatible; the schema migrates itself at startup. **Every PostgreSQL user
of 1.x should upgrade** — the 1.x feeds can skip records.

Upgrade notes — what a consumer of 1.4.x can notice:

- **Rebuild your projections after upgrading.** Up to 1.4 the PostgreSQL feeds could
  skip changes and events for good (see *Fixed* below). Upgrading stops new losses; it
  cannot deliver what was skipped before. Reset every handler whose output must be
  complete (`ResetCheckpointAsync`) and let it replay. Effect handlers (mails, webhooks)
  may have missed work — the records themselves are intact in `papuma.change` /
  `papuma.event`, so a one-off query can find what was never acted on.
- **Delivery order is causal order, not global `seq` order.** A handler receives a
  transaction's records after those of every transaction that committed before it
  started; concurrent transactions come in no guaranteed order; per document strictly
  by version. A lower `seq` can now arrive after a higher one. Handlers that use `seq`
  as a watermark ("skip everything ≤ the highest seen") drop records — use the
  documented idempotency (handler + `seq`, or the document version) instead. Handlers
  must not decide anything from the order of unrelated documents: live delivery and a
  rebuild may order concurrent transactions differently (concepts §2).
- **New `txid` indexes** on `papuma.change` and `papuma.event` (`ix_papuma_change_txid`,
  `ix_papuma_event_txid`) and four columns on `papuma.checkpoint` are created at the
  first startup by `EnsureSchemaAsync`; existing checkpoints are carried over. On large
  tables create the indexes beforehand without blocking writes:
  `CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_papuma_change_txid ON papuma.change (txid);`
  (and the same for `papuma.event`).
- **Direct-SQL feed consumers must switch queries.** The read prescribed by
  `feed-wire-format.md` up to 1.4 (`seq > checkpoint AND txid < xmin`) has the same
  defect. Section 4 now specifies the snapshot cursor; the Python and Go samples use it.
- **`ChangeFeedLagSnapshot.Lag` is a count** of committed, undelivered records;
  `Checkpoint` is the highest delivered `seq`. Open transactions are no longer lag.
- **After a logical restore** (`pg_dump`/`pg_restore` into another cluster)
  `EnsureSchemaAsync` repairs the feed state (transaction ids are per cluster); records
  delivered shortly before the dump may be delivered again (at-least-once).
  `Papuma.Kernel.Local` is unaffected by all of the above.

Changes:

- **Fixed (PostgreSQL): the change and event feeds could skip records of interleaved
  transactions.** The read predicate `seq > checkpoint AND txid < pg_snapshot_xmin(…)`
  assumed that an older transaction also draws its sequence numbers earlier. A session
  writing several times breaks that: A writes (seq 96), B writes (97), A writes again
  (98) and commits — the checkpoint moved to 98 while B was open, and B's 97 was never
  delivered, by any handler, without a failure entry (jejak feedback F-15). No predicate
  over a sequence checkpoint can fix this; a handler's position is now a transaction
  snapshot (ADR-022, the PgQ model): each cycle delivers the transactions committed
  since, as a slice in `seq` order. Proven by a concurrency stress test (interleaved
  multi-write sessions, random commit and rollback, two competing processors) that
  fails on the old engine on every run.
- **A long-open write transaction no longer stalls the feed.** Under the old horizon
  any open transaction held back every later commit; now it holds back only its own
  records.
- **New: `SchemaManager.RepairFeedAfterLogicalRestoreAsync`** — the restore repair that
  `EnsureSchemaAsync` runs, callable directly when the schema is managed elsewhere.
- **Docs:** ADR-022 (snapshot cursor; supersedes point 2 of ADR-010, amends ADR-009's
  ordering), concepts §2 rewritten, feed-wire-format §4 rewritten; ADR-023 (why there is
  no event-sourcing mode) with the stream-shaped aggregates recipe.

## 1.4.0 (2026-09-27)

Upgrade notes — what a consumer of 1.3.x can notice:

- **A new index on `papuma.change`** (`ix_papuma_change_correlation`) is created at the
  first startup by `EnsureSchemaAsync`. A plain `CREATE INDEX` blocks writes to the table
  while it builds; on a large change table, create it beforehand without blocking:
  `CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_papuma_change_correlation ON papuma.change
  (scope, tenant_id, (metadata ->> 'correlationId'));` — startup then finds it in place.
- **`Papuma.Kernel.Local` feed handlers now run with no transaction open.** Handlers that
  write to the same database file start working; nothing else changes for correct
  handlers. Run one feed processor per database file (the hosted service does).
- **`SqliteChangeNotifier.WaitAsync` is obsolete** — compiler warning; wait on
  `Subscribe()` instead.
- **Retry timing on PostgreSQL follows the database clock.** Retries no longer shift by
  the clock skew between application and database hosts.

Changes:

- **Fixed (`Papuma.Kernel.Local`): handlers could not write to the database file.** The
  feed processors held a `BEGIN IMMEDIATE` transaction — the file's single write lock —
  while handlers ran, so a local projection writing through its own connection got
  `database is locked` (then retry, then poison), and any slow handler (mail, HTTP)
  blocked every application write for its duration. Both processors now read a batch
  in a short transaction, run the handlers with no transaction open, and record the
  outcome in a second short one — only if the checkpoint was not moved meanwhile (a
  reset for a rebuild wins; the stale outcome is dropped and re-read). At-least-once,
  ordering and stop-the-line are unchanged. The old lock also serialized a second
  processor instance on the same file; run one per file (the hosted service does).
- **Fixed (`Papuma.Kernel.Local`): the event feed missed wakeups.** Change and event
  processor shared one single-slot signal; whichever read it first took it, the other
  slept until the poll interval (default 5 s). `SqliteChangeNotifier.Subscribe()` gives
  every waiter its own buffered signal and a commit reaches all of them;
  `SqliteChangeNotifier.WaitAsync` is obsolete for that reason.
- **Fixed (PostgreSQL): retry backoff mixed two clocks.** `next_retry_at` was set with
  the database clock and compared with the application host's clock, shifting every
  retry by the clock skew between the two (a few ms in a container were enough to make
  zero-delay retries wait). The due check now runs in the database
  (`next_retry_at > now()`), for the change and the event feed.
- **`GetChangesByCorrelationAsync(correlationId)`** on both kernels' sessions: every
  change a unit of work produced, across document types, in feed order, scope-bound —
  "what did this command do?" for audit timelines and for tests asserting a command's
  complete effect. Backed by a new index on the change table's `correlationId`
  (created idempotently by `EnsureSchemaAsync`). jejak feedback F-12.
- **MCP: `get_changes_by_correlation`** — the correlation read as a read-only tool, so an
  agent can answer "what did this command do?" from any change's `correlationId`
  (policy-applied diffs, scope-bound, like `get_document_history`).
- **`ISchemaContributor` / `AddSchemaContributor<T>()`**: application schema
  (projection tables, their RLS policies) applied at startup after the kernel schema
  and before the feed workers — so policies can use the ADR-019 functions and handlers
  never run against a missing table. New recipe
  [schema for projection tables](docs/recipes/projection-schema.md): idempotent DDL,
  an advisory lock for concurrent starts, and breaking changes as a rebuild through a
  new handler name. Verified by `ProjectionSchemaTests`. jejak feedback F-11.
- **`Papuma.Kernel.Local`: schema contributors** — `AddSchemaContributor<T>()` with an
  `ISqliteSchemaContributor` (the kernel's open `SqliteConnection`), run after the
  kernel schema and before the feed workers, like the Postgres hook.
- **Write-path storage probe** (`dotnet run -c Release -- writepath` in
  `benchmarks/Papuma.Kernel.Benchmarks`): saves/s, p50/p95, WAL per save, HOT ratio and
  table/index growth for 5–50 KB incompressible documents, 0 vs 3 declared keys, 1/16/32
  sessions, patch and full save, plus `fillfactor` 90 and `lz4` TOAST compression — a
  fresh PostgreSQL 18 container per scenario, `--rounds`/`--filter` for repeat
  measurements. Result in concepts §14: declared keys take HOT from 92–100 % to 0 %, but
  at realistic shape that costs 0–9 % WAL and no measurable throughput — the trigger for
  the deferred key side table did not fire. jejak feedback F-14.
- **Docs: write-path storage costs** (concepts §14). Measured: one declared key anywhere
  in the model makes HOT updates impossible for every document (0 % vs 52–66 % without
  keys), because keys are expression indexes over the always-changing `data` column.
  Document size is now documented as a modelling cost (whole-document rewrite per save;
  `default_toast_compression = lz4` for large documents). A key side table and
  partitioning of `papuma.change` are deferred with measurable triggers; tenant-based
  distribution is noted as the long-range route. jejak feedback F-14.
- **Docs: the raw feed is an in-application contract.** concepts §21, the feed wire
  format, the playbook, getting-started and both factsheets now separate the stable
  *format* from the *content* (your documents' field paths) and point other teams and
  systems to explicit integration events at the boundary. jejak feedback F-13.
- **ADR-021 evidence log** records the first consumer data point for a testing-package
  xUnit adapter (jejak, F-10) and that the correlation read API it listed as a
  prerequisite now exists.

## 1.3.0 (2026-09-26)

Upgrade notes — what a consumer of 1.2.x can notice:

- **MCP over HTTP is stateless** (ModelContextProtocol 2.x): no `Mcp-Session-Id` is
  issued, and a request carrying one gets 400. Set `Stateless = false` on
  `WithHttpTransport` if a client needs sessions.
- **`SetQ`/`RemoveQ`/`IncrementQ` (F#) are obsolete** — compiler warnings, still
  working; switch to `p.Set((fun (x: T) -> x.Field), v)` and friends.
- **`KeyPath` of a composite key is comma-separated** (`projectId,number`) — code
  that parses `UniqueKeyViolationException.KeyPath` as one dot-path should use
  `KeyPaths`. Single-field keys are unchanged.
- **`ScopeContext.Tenant(null)` throws `ArgumentNullException`** naming `tenantId`;
  the accepted id pattern only widened.
- **Packaged docs moved** from `docs/vNEXT/` to `docs/`.

Changes:

- **New package `Papuma.Kernel.Testing` (ADR-021).** `PapumaTestDatabase` starts
  PostgreSQL 18 via Testcontainers (or connects to an existing server) and adds a
  login role without superuser or `BYPASSRLS`, so tests exercise row-level security as
  production does — a superuser connection passes even when isolation is broken.
  `CreateStoreAsync(model)` applies schema and grants; `GrantAppRoleAsync(schema)`
  covers application tables. `DrainAsync()` on `ChangeFeedProcessor` and
  `EventFeedProcessor` runs a feed to quiescence and throws `FeedDrainException` when a
  handler failed (a stopped feed otherwise just delivers nothing) or the feed does not
  settle. Test-framework agnostic; the kernel's own suite now runs on it. A broader
  package (framework adapters, change assertions) is deferred against objective
  triggers named in the ADR. getting-started §7 and concepts §32 describe the setup.
- **Composite keys (ADR-020).** `UniqueKey`/`LookupKey` accept an anonymous type —
  `.UniqueKey(x => new { x.ProjectId, x.Number })` — materialized as one multi-expression
  partial index on PostgreSQL and SQLite. New overload
  `LoadByKeyAsync<T>(key, IReadOnlyList<object> values)` looks up with one value per
  component. Like single-field keys, a composite key is enforced only for documents
  that carry every component. `PatchWhereAsync`, `DeleteWhereAsync` and masked reads by
  key stay single-field and reject composite keys. `KeyMetadata` gains `Paths`,
  `IsComposite` and `ComponentSegments`; `UniqueKeyViolationException` gains `KeyPaths`.
  For composite keys, `Path`/`KeyPath` hold the component paths comma-separated;
  single-field keys are unchanged. The kernel never drops indexes: replacing a
  flattened helper-field key leaves its old index until you drop it.
- **Row-level security for your own tables (ADR-019).** `EnsureSchemaAsync` now creates
  `papuma.scope_visible(scope, tenant_id)` and `papuma.scope_writable(scope, tenant_id)`
  — the kernel's scope rules as SQL functions, to be used in RLS policies on
  application tables (typically projection targets) in the same database. They are
  the public contract; the underlying setting names stay internal. A test pins them to
  the kernel's own policies. New recipe
  [read models in the same database](docs/recipes/same-database-read-models.md),
  verified by `ScopeFunctionTests`, and concepts §30.
- **`SaveResult.GetDocument<T>()`** returns the document as persisted by the write —
  in particular the value a `PatchAsync` `Increment` produced, without a second read and
  without the conflict window of load + save (sequence numbers, ticket keys). Unlike
  `Diff` it is not policy-applied and holds every field. Works for patch, save and
  rollback results in both kernels; throws after a delete or for the wrong type.
  concepts §17 gains a "sequence numbers" paragraph.
- **Tenant ids accept GUIDs.** `ScopeContext.Tenant` now accepts
  `^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$` (was `^[A-Za-z][A-Za-z0-9_]{1,100}$`): a leading
  digit and `-` are allowed, so GUID strings work without a prefix. Every previously
  valid id stays valid. The pattern was a whitelist, never an SQL-safety measure —
  tenant ids only reach SQL as parameters. New overload `ScopeContext.Tenant(Guid)`
  yields the canonical lowercase dashed form (ids compare case-sensitively) and rejects
  `Guid.Empty`. A null id now throws `ArgumentNullException` naming `tenantId`.
- **F#: key declarations from lambdas work.** F# lowers `fun x -> box x.Email` to a
  call to `Operators.Box` instead of C#'s `Convert` node, so `UniqueKey`, `LookupKey`,
  `Property`, `LoadByKeyAsync` and the key-based bulk operations rejected every F#
  lambda; only `[<UniqueKey>]` attributes worked. The path resolver now unwraps F#'s
  `box` too. Composite keys from F# use a tuple (`box (x.ProjectId, x.Number)`);
  anonymous records are rejected, because F# sorts their fields. The F# README,
  playbook and snippet show the forms; F# tests cover them end to end.
- **F#: `SetQ`/`RemoveQ`/`IncrementQ` deprecated.** They rested on the premise that F#
  cannot turn lambdas into LINQ expressions; it can. The real obstacle was the
  unrecognized F# `box` (fixed above), so the kernel's own Patch API now works from F#:
  `p.Set((fun x -> x.Name), v).Remove(fun x -> box x.Note).Increment((fun x -> box x.Count), n)`.
  The quotation members still work, are marked `[<Obsolete>]` and are planned for
  removal in 2.0. Docs, the slice conventions and the `fsharp-local-todo` sample use
  the lambda form; concepts §29 records the correction. One F# habit comes with it:
  annotate the lambda parameter (`fun (x: TodoItem) -> x.Done`) unless a type argument
  fixes the document type — F# otherwise infers the most recently declared record type
  with that field name (running the sample caught exactly this).
- **Dependencies:** `ModelContextProtocol` 1.4 → 2.2 (`Papuma.Kernel.Mcp`, sample),
  `Microsoft.Extensions.*` 10.0.0 → 10.0.12, `Microsoft.Data.Sqlite` 10.0.10 → 10.0.12
  (the `SQLitePCLRaw` 3.0.5 pin stays), test tooling (Test SDK 18, xunit runner 4,
  coverlet 10), GitHub Actions (checkout v7, setup-dotnet v6, upload-artifact v7).
  **MCP over HTTP is now stateless by default** — no `Mcp-Session-Id`, and a request
  carrying one gets 400. The Papuma tools hold no session state; clients that need
  sessions set `Stateless = false` (observability.md). The sample's
  `diagnostics.http` drops the session-header handshake.
- **Packaging: one doc set in every package, with no dangling links.** All packages
  (`Papuma.Kernel`, `.Local`, `.FSharp`, `.AspNetCore`, `.Mcp`, and the new `.Testing`) now ship the same
  `docs/` set — guides, ADRs, recipes, the `ai/` set and `analyses/` — defined once in
  `Directory.Build.targets`. Before, each package shipped its own subset, and links
  between documents dangled depending on the package (e.g. `concepts.md` →
  `getting-started.md` inside `Papuma.Kernel.Local`). PostgreSQL-only pages now say so at
  the top and point `Papuma.Kernel.Local` users to the playbook's Differences section.
  Links to files outside the set (samples, `legacy/`, repo files) are absolute GitHub
  URLs; the package READMEs link only absolutely, since nuget.org resolves no relative
  link (this also fixes the logo there). A new test (`PackagedDocsLinkTests`) fails on
  any link or heading anchor that would dangle inside a package. The AGENTS snippet now
  tells agents to read the docs from the package folder of the version they build
  against.
- **Packaging**: packages now carry the project icon, a README, Source Link metadata
  (`PublishRepositoryUrl`, `EmbedUntrackedSources`, deterministic CI builds), XML
  documentation for IntelliSense, and a `.snupkg` symbol package.
- **Docs: consumer feedback from jejak.** `getting-started.md` gains §7 "Without a
  host" (hand-built store, `ChangeFeedProcessor.ProcessOnceAsync`; the integration-test
  part later moved to `Papuma.Kernel.Testing`) and documents the tenant id pattern and
  keys (both later extended — GUID tenant ids, composite keys; see above). concepts §17
  now states that `Increment` is safe against concurrency but not against duplicate
  commands. The playbook carries these points and no longer links to files that are
  not shipped in the `Papuma.Kernel` package.
- **Docs:** the AGENTS snippet names the RLS functions and the testing package in its
  rules; slice conventions and the event-modeling recipe point to
  `Papuma.Kernel.Testing`; `llms.txt` is current (32 concepts sections, 21 ADRs, the
  testing package, the shared doc set).
- **Docs reorganised for the public repository.** The `vNEXT` working title is gone:
  `docs/vNEXT/*` moved up to `docs/` (guides flat, `docs/adr/`, `docs/recipes/`), and
  every frozen pre-1.0 document — the German v1 implementation set, the three v1-era
  ADRs, the v1 change-feed DSL spec, the German `concepts.de.md`, the v1 assessment and
  polling analysis, and the 13-phase implementation plan — now lives under
  `docs/legacy/`, which states plainly that it is not maintained.
  [`docs/README.md`](docs/README.md) is the new map. **Links into `docs/vNEXT/` or
  `docs/v1/` no longer resolve**; the packaged doc set moved the same way
  (`docs/vNEXT/concepts.md` → `docs/concepts.md` inside the NuGet packages).
- **Dependencies**: `Testcontainers.PostgreSql` 4.1.0 → 4.15.0, which drops the
  transitively vulnerable `SSH.NET` 2024.1.0 (GHSA-q939-rpr3-3284). Test-only.
- **Build is warning-free again**: resolved XML `cref` references that broke once
  documentation generation was enabled, and the obsolete `PostgreSqlBuilder()` constructor.
- **Project files**: added `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`,
  issue/PR templates, Dependabot, `.editorconfig` and `.gitattributes`. The internal
  security/architecture review pair was removed from the repository root.

## 1.2.1 (2026-09-07)

- **Docs**: closed the same gap for F# that opened for `Papuma.Kernel.Local`
  in 1.1.0 — `llms.txt` and the `docs/ai/` set were entirely silent on
  `Papuma.Kernel.FSharp` and would have misled a coding agent building an F#
  app. Added, all additive (no restructuring, same pattern as the existing
  `Papuma.Kernel.Local`-differences sections):
  - Playbook: a "Using `Papuma.Kernel.FSharp`" section (`SetQ`/`RemoveQ`/
    `IncrementQ`, `trySaveAsync`/`tryPatchAsync`, `runSession`, the
    `CommitAsync` and no-DU/`option` rules).
  - AGENTS.md snippet: an F# addendum on top of either kernel's block.
  - Slice conventions: a full F# translation of the Command slice (the one
    that actually diverges — Patch needs quotations, minimal-API needs a
    `Func<_,_>` delegate wrapper), plus the Registration/Test idiom
    differences; View/Automation/Translation follow the same pattern.
  - `llms.txt`: points agents at all of the above and at the
    `Papuma.Kernel.FSharp` README.
  - `Papuma.Kernel.FSharp` now bundles the same `docs/ai/` set and
    `concepts.md` its sibling packages do, so the guidance is on disk after
    `dotnet restore` without needing to know to look in another package's
    folder.

## 1.2.0 (2026-09-05)

- **New package: `Papuma.Kernel.FSharp`** — an F#-idiomatic facade over the
  write path, additive on top of `Papuma.Kernel.Core` (no kernel change).
  Quotation-based `Patch` (`SetQ`/`RemoveQ`/`IncrementQ`) via a hand-rolled
  quotation-to-`Expression<Func<T,TValue>>` walker — `<@ fun x -> x.Field @>`
  converts directly, no `Func<_,_>` wrapper needed. `Result<'T, KernelError>`
  instead of exceptions for the three expected write outcomes ADR-003/006
  document (`VersionConflict`/`DocumentNotFound`/`UniqueKeyViolation`) via
  `trySaveAsync`/`tryPatchAsync` — written against statically resolved type
  parameters (SRTP) so one implementation covers both `Papuma.Kernel`
  (Postgres) and `Papuma.Kernel.Local` (SQLite), which share no common
  interface, only identical method shapes. `runSession` fills the
  `IAsyncDisposable` gap F#'s `use` doesn't cover. Deliberately does not
  support discriminated-union or `option` fields on document types — the
  kernel's JSON serializer config stays fixed on purpose (one deterministic
  wire format across every language and process); see
  `docs/concepts.md` §29 for the full reasoning and the model-at-the-
  boundary alternative.
- **New sample: `samples/fsharp-local-todo`** — the first sample against
  `Papuma.Kernel.Local` (the other three are Postgres-only), a minimal F#
  ASP.NET Core API exercising the full `Papuma.Kernel.FSharp` facade:
  `[<UniqueKey>]` on a record field, `SetQ`/`IncrementQ` in one `Patch` call,
  `trySaveAsync`/`tryPatchAsync` mapped to HTTP 409/404, `runSession`, and an
  F#-authored `IChangeHandler` on the feed.
- **`publish-nuget.sh` retargeted at GitHub Packages** (was hardcoded to a
  private Gitea instance) — the GitHub owner is now auto-detected from the
  `origin` remote instead of a hardcoded default, so the script works
  unchanged in any GitHub repo. Project discovery now also picks up
  `.fsproj` packages, not just `.csproj`.

## 1.1.0 (2026-08-11)

- **New package: `Papuma.Kernel.Local`** — a SQLite-backed sibling of
  `Papuma.Kernel` for single-writer desktop/embedded use, at full parity with
  the Postgres kernel's public API (Load/Save/Delete/versioning/diff/change
  feed, Patch and bulk operations, GDPR redaction, masked reads, history,
  rollback, feed processing, hosting via `AddPapumaKernelLocal`). Same
  document-sourcing model, no server required. See
  `docs/analyses/local-kernel-sqlite-sibling.md` for the design rationale
  and what's deliberately different per engine (no RLS, no gapless-read
  handling, in-process wakeup instead of LISTEN/NOTIFY — none of that
  applies to a single writer). Every connection now goes through a shared
  `SqliteConnectionFactory` that enables WAL journal mode and a 5s busy
  timeout, so a background feed processor writing doesn't block the UI
  reading chat/document history (verified empirically, not assumed — see
  `SqliteConnectionFactoryTests`).
- **Fix**: declared keys (`UniqueKey`/`LookupKey`) on non-string fields
  (numbers, booleans) now work correctly on `Papuma.Kernel.Local` —
  `LoadByKeyAsync`/`PatchWhereAsync`/`DeleteWhereAsync` previously compared
  every key value as TEXT, but SQLite's `json_extract` returns
  INTEGER/REAL/0-1 for JSON numbers and booleans and never considers those
  equal to TEXT, so non-string keys silently matched nothing. Also fixed:
  the declared-key expression index was never actually used by any of these
  lookups (SQLite only matches an expression index when the query's
  expression is textually identical to the index's, and the JSON path was
  being bound as a parameter, not a literal) — every keyed lookup, including
  ones that "worked" on string keys, was doing a full table scan. Both
  confirmed empirically and fixed; see `SqliteSpikeTests` and
  `SqliteNumericKeyTests`.
- **Internal restructuring**: extracted the storage-neutral diff engine, policy
  engine, model, and validation code (previously `Changes/`, `Model/`,
  `Validation/`, and the storage-neutral half of `Tenancy/`) into a new
  `Papuma.Kernel.Core` project, referenced by `Papuma.Kernel` and embedded in
  its package (`Papuma.Kernel.Core.dll` ships inside the `Papuma.Kernel`
  nupkg, not as a separate published package). Preparation for a future
  SQLite-backed sibling for desktop/embedded use
  (`docs/analyses/local-kernel-sqlite-sibling.md`). No public API or behavior
  change; namespaces are unchanged.
- **Docs**: `README.md` and `docs/factsheet*.md` rewritten to position
  `Papuma.Kernel.Local` as an equal-billing second product rather than a
  footnote — shared/Postgres-only/SQLite-only feature grouping, a
  `Papuma.Kernel.Local` maturity statement, dual quickstart snippets, and a
  new §8 in the technical factsheet. `llms.txt` and the `docs/ai/` set
  (playbook, AGENTS.md snippet, slice conventions) updated for the same
  reason — they were entirely Postgres-framed and would have misled a coding
  agent building against `Papuma.Kernel.Local`. The `Papuma.Kernel.Local`
  package itself now bundles its own docs (README, `docs/ai/`, the design
  rationale, `concepts.md`, `gdpr.md`, the ADRs) the same way `Papuma.Kernel`
  already did — previously it shipped none, contradicting what the docs
  claimed.

## 1.0.2 (2026-06-22)

- **Docs** (shipped inside the package under `docs/`): reframed the read-path
  guidance so a projection is the default derived read and a SQL view is a gated
  exception, not a peer choice (concepts §16, the AI agent snippet and playbook).
  Clarified that "real-time / no lag" alone points to `LoadByKeyAsync`, and added a
  structural-hardening note (`LoadMaskedAsync` / ADR-016). No functional change.

## 1.0.1 (2026-06-14)

- **License headers switched to SPDX** across all source files
  (`SPDX-License-Identifier: MIT` + `SPDX-FileCopyrightText: 2026 Harald Lapp`),
  replacing the prose copyright/license comment. Machine-readable for license
  scanners and SBOM tooling; no functional change.
- **REUSE: added `LICENSES/MIT.txt`** with the full license text alongside the
  SPDX headers.
- **Docs** (shipped inside the package under `docs/`): added a learning-oriented
  [tutorial](docs/tutorial.md) (empty folder → feed-driven read model,
  verified end-to-end against PostgreSQL 18); the slice conventions now document
  registration co-located with each slice; modernized the root README and
  refreshed the factsheet.

## 1.0.0 (2026-06-12) — the 1.0 reboot

**Complete rewrite. No migration path from 0.x — this is a different kernel under the
same name.** The 0.x line (relational tables + outbox + explicitly raised events) is
removed; 1.0 is document-sourced CQRS:

- **Documents are the source of truth** (JSONB), the change feed is derived
  automatically as a reversible field diff per write — no ORM, no triggers, no
  hand-written outbox entries (ADR-002/004).
- **PostgreSQL ≥ 18 required**: the write path is a single atomic statement using
  `RETURNING OLD/NEW` with mandatory optimistic concurrency (ADR-001/003).
- **Privacy policies as a kernel concern**: `[SensitiveData]`, `[TrackHash]`,
  `[TrackReference]`, `[DoNotTrack]` (attributes as defaults, fluent overrides) are
  applied before anything reaches the feed — including delete diffs (ADR-007).
- **Schema evolution from day one**: per-type `schema_version`, lazy upcaster chains,
  loud guards against silent back-migration (ADR-005).
- **Sessions are units of work** with per-write savepoints, shared `correlationId`,
  and append-only `RollbackAsync` (ADR-008).
- **Patch and bulk primitives**: `jsonb_set`-based partial updates without loading,
  set-based `PatchWhere`/`PatchMany`/`DeleteWhere`/`DeleteMany` (ADR-012/014).
- **Processing engine**: gapless snapshot reads (`xid8`), NOTIFY wakeup with polling
  as truth, strict per-handler ordering, retry/backoff/poison, checkpoints with
  `FOR UPDATE SKIP LOCKED` leader coordination, rebuild, lag health check (ADR-009/010).
- **Append-only event log** for facts without state truth (`session.AppendAsync`),
  consumed by the same engine, with opt-in per-type retention (ADR-011/013).
- **Bootstrap**: `services.AddPapumaKernel(...)` hosts schema setup, feed workers and
  retention; multi-tenancy (scope predicates + RLS) carried over from 0.x.

Docs: `docs/` (architecture, 14 ADRs, concepts, getting started, recipes).

## 0.0.1-legacy

Final release of the v1 kernel (PostgreSQL event feed, outbox, projections,
GDPR redaction). Superseded by the 1.0 reboot.
