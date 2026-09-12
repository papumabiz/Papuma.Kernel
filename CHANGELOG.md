# Changelog

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
