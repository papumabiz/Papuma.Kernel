# Changelog

## Unreleased

- **Dependencies:** `ModelContextProtocol` 1.4 → 2.2 (`Papuma.Kernel.Mcp`, sample),
  `Microsoft.Extensions.*` 10.0.0 → 10.0.12, `Microsoft.Data.Sqlite` 10.0.10 → 10.0.12
  (the `SQLitePCLRaw` 3.0.5 pin stays), test tooling (Test SDK 18, xunit runner 4,
  coverlet 10), GitHub Actions (checkout v7, setup-dotnet v6, upload-artifact v7).
  **MCP over HTTP is now stateless by default** — no `Mcp-Session-Id`, and a request
  carrying one gets 400. The Papuma tools hold no session state; clients that need
  sessions set `Stateless = false` (observability.md). The sample's
  `diagnostics.http` drops the session-header handshake.
- **F#: `SetQ`/`RemoveQ`/`IncrementQ` deprecated.** They rested on the premise that F#
  cannot turn lambdas into LINQ expressions; it can. The real obstacle was the
  unrecognized F# `box` (fixed below), so the kernel's own Patch API now works from F#:
  `p.Set((fun x -> x.Name), v).Remove(fun x -> box x.Note).Increment((fun x -> box x.Count), n)`.
  The quotation members still work, are marked `[<Obsolete>]` and are planned for
  removal in 2.0. Docs, the slice conventions and the `fsharp-local-todo` sample use
  the lambda form; concepts §29 records the correction. One F# habit comes with it:
  annotate the lambda parameter (`fun (x: TodoItem) -> x.Done`) unless a type argument
  fixes the document type — F# otherwise infers the most recently declared record type
  with that field name (running the sample caught exactly this).
- **F#: key declarations from lambdas work.** F# lowers `fun x -> box x.Email` to a
  call to `Operators.Box` instead of C#'s `Convert` node, so `UniqueKey`, `LookupKey`,
  `Property`, `LoadByKeyAsync` and the key-based bulk operations rejected every F#
  lambda; only `[<UniqueKey>]` attributes worked. The path resolver now unwraps F#'s
  `box` too. Composite keys from F# use a tuple (`box (x.ProjectId, x.Number)`);
  anonymous records are rejected, because F# sorts their fields. The F# README,
  playbook and snippet show the forms; F# tests cover them end to end.
- **Docs:** the AGENTS snippet names the RLS functions and the testing package in its
  rules; slice conventions and the event-modeling recipe point to
  `Papuma.Kernel.Testing`; `llms.txt` is current (32 concepts sections, 21 ADRs, the
  testing package, the shared doc set).
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
- **Packaging: one doc set in every package, with no dangling links.** All packages
  (`Papuma.Kernel`, `.Local`, `.FSharp`, `.AspNetCore`, `.Mcp`) now ship the same
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
- **Docs: consumer feedback from jejak.** `getting-started.md` gains §7 "Without a
  host" (hand-built store, `ChangeFeedProcessor.ProcessOnceAsync`, the integration-test
  shape the kernel's own suite uses), the tenant id pattern and the single-path limit
  of keys (with the flattening workaround). concepts §17 now states that `Increment` is
  safe against concurrency but not against duplicate commands. The playbook carries
  all four points and no longer links to files that are not shipped in the
  `Papuma.Kernel` package; `ScopeContext.Tenant` documents its pattern and exception.
- **Docs reorganised for the public repository.** The `vNEXT` working title is gone:
  `docs/vNEXT/*` moved up to `docs/` (guides flat, `docs/adr/`, `docs/recipes/`), and
  every frozen pre-1.0 document — the German v1 implementation set, the three v1-era
  ADRs, the v1 change-feed DSL spec, the German `concepts.de.md`, the v1 assessment and
  polling analysis, and the 13-phase implementation plan — now lives under
  `docs/legacy/`, which states plainly that it is not maintained.
  [`docs/README.md`](docs/README.md) is the new map. **Links into `docs/vNEXT/` or
  `docs/v1/` no longer resolve**; the packaged doc set moved the same way
  (`docs/vNEXT/concepts.md` → `docs/concepts.md` inside the NuGet packages).
- **Packaging**: packages now carry the project icon, a README, Source Link metadata
  (`PublishRepositoryUrl`, `EmbedUntrackedSources`, deterministic CI builds), XML
  documentation for IntelliSense, and a `.snupkg` symbol package.
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
