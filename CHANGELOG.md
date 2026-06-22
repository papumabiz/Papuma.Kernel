# Changelog

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
  [tutorial](docs/vNEXT/tutorial.md) (empty folder → feed-driven read model,
  verified end-to-end against PostgreSQL 18); the slice conventions now document
  registration co-located with each slice; modernized the root README and
  refreshed the factsheet.

## 1.0.0 (2026-06-12) — the vNEXT reboot

**Complete rewrite. No migration path from 0.x — this is a different kernel under the
same name.** The 0.x line (relational tables + outbox + explicitly raised events) is
removed; vNEXT is document-sourced CQRS:

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

Docs: `docs/vNEXT/` (architecture, 14 ADRs, concepts, getting started, recipes).

## 0.0.1-legacy

Final release of the v1 kernel (PostgreSQL event feed, outbox, projections,
GDPR redaction). Superseded by the vNEXT reboot.
