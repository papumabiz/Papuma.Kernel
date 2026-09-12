# ADR-001: PostgreSQL ≥ 18 as the only target database

## Status

Accepted (2026-06-11)

## Context

The original design discussion sketched a DB-agnostic core library with
provider packages (`Papuma.Postgres`, `Papuma.SqlServer`, ...). The kernel,
however, lives off features that only PostgreSQL offers in this combination:

- **JSONB** with operators and expression indexes (keys/constraints on document fields),
- **`RETURNING OLD/NEW`** (PostgreSQL 18+): old and new state atomically in one
  statement — the foundation of the write path (ADR-003),
- **LISTEN/NOTIFY** as the wakeup for feed consumption (ADR-010),
- **`pg_current_xact_id()` / snapshot functions** for gapless feed reads (ADR-010).

A provider abstraction would have to flatten all of this to the lowest common
denominator or maintain special paths per provider — both dilute the design before
a single user for a second provider exists.

## Decision

1. Papuma Kernel supports exclusively **PostgreSQL, minimum version 18**.
2. There is **no `IStorageProvider` interface** and no provider packages.
   SQL lives directly in the kernel.
3. The minimum version is checked at startup (`SHOW server_version_num`,
   < 180000 → meaningful exception).

## Consequences

- The write path may rely unconditionally on `RETURNING OLD/NEW`; no fallback
  code path "load the old document first".
- Whoever needs a different database needs a different product — that is a
  deliberate positioning, not a gap.
- Should the need arise later after all, extracting an abstraction from a working
  Postgres implementation is easier than the reverse approach.
