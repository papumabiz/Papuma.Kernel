# ADR-021 — A narrow testing package

## Status

Accepted (2026-09-25) — the narrow package is implemented; a broader one is
deferred against the triggers below

## Context

Every application on the kernel writes the same integration-test setup: a
PostgreSQL 18 container, `EnsureSchemaAsync`, and a way to run feed handlers
deterministically. Consumer feedback (jejak, F-7) asked for a package with a
fixture, a fresh-tenant helper and GIVEN/WHEN/THEN assertions.

Two parts of that setup are not convenience but correctness:

- **The connecting role.** The Testcontainers default user is a superuser, and
  superusers bypass row-level security. A test suite on that connection passes
  even when tenant isolation is broken — and with ADR-019, applications rely on
  RLS for their own tables too.
- **Handler failures while draining.** A throwing handler stops its feed
  (stop-the-line); `ProcessOnceAsync` then delivers nothing and returns 0. A
  hand-written "process until 0" loop reports success over the exception.

Other parts are opinion: which test framework, which assertion style. The
kernel's own suite is xUnit; consumers use xUnit, NUnit, MSTest or Expecto. The
event-modeling recipe already places decision logic in in-memory tests, leaving
only a few slice tests against the database.

## Decision

1. A package `Papuma.Kernel.Testing`, **test-framework agnostic**, with exactly:
   - `PapumaTestDatabase` (`IAsyncDisposable`): `StartAsync` (Testcontainers,
     `postgres:18-alpine` by default) or `ConnectAsync` (an existing server);
     creates a login role with `NOSUPERUSER NOBYPASSRLS`; exposes
     `OwnerDataSource` for setup and inspection and `AppDataSource` for the code
     under test; `EnsureSchemaAsync(model)` / `CreateStoreAsync(model)` apply the
     schema and grants; `GrantAppRoleAsync(schema)` covers application tables.
   - `DrainAsync()` on `ChangeFeedProcessor` and `EventFeedProcessor`: process
     until a cycle delivers nothing, then throw `FeedDrainException` if any of the
     processor's handlers recorded a failure; a cycle limit stops handlers that
     keep producing records.
2. The kernel's own `PostgresFixture` is built on `PapumaTestDatabase`, so the
   package is exercised by the full suite on every run.
3. No fresh-tenant helper: `ScopeContext.Tenant(Guid.NewGuid())` already is one.
4. No SQLite counterpart: `Papuma.Kernel.Local` needs no container — a temporary
   file plus `SqliteSchemaManager.EnsureSchemaAsync` is the whole setup.
5. The public surface is one class, one extension class and one exception — no
   base classes or interfaces a later extension would have to keep compatible.

## Deferred — and when to revisit

A broader package is **not** built speculatively. Revisit when either holds:

- **Two independent consumer repositories** contain an equivalent helper built on
  `Papuma.Kernel.Testing` (the same assertion, the same fixture wrapper), or
- a consumer feedback entry asks for a specific helper **with the code it
  replaces**.

Candidates, so the revisit does not start from zero:

- **Framework adapters** as separate packages (e.g. `Papuma.Kernel.Testing.Xunit`
  with a ready collection fixture) — never inside this package.
- **Asserting a command's changes by correlation id.** Needs a kernel read API
  for changes by `correlationId` first (today: `GetHistoryAsync` per document) —
  a kernel decision before a testing one.
- **Time control for timer slices** (`dueAt`) via `TimeProvider`.

## Consequences

- **Positive:** The correctness traps — superuser connections, swallowed handler
  failures, unbounded drains — are handled once, in tested code.
- **Positive:** Works with any test framework; the xUnit wiring is a few lines.
- **Negative:** Consumers inherit the `Testcontainers.PostgreSql` dependency and
  its update cadence (it needed a security bump in 1.2.x).
  `ConnectAsync` is the way out when a container runtime is unwanted.
- **Negative:** Roles are server-wide. Parallel runs sharing one server through
  `ConnectAsync` need distinct role names.
- **Neutral:** Test isolation stays the tests' job: a fresh tenant per test, a
  unique handler name per processor, and document type names unique across test
  classes (key indexes are per document type, database-wide).

## Alternatives considered

- **Documentation only** (getting-started §7 already describes the setup). Cheap,
  but the two correctness traps stay copy-paste material that each consumer can
  get wrong.
- **A GIVEN/WHEN/THEN DSL now.** Fixes an API before any real usage pattern
  exists; the recipe's split (in-memory decisions, few slice tests) needs little
  of it.
- **An xUnit-based package.** Simplest to write, unusable for NUnit/MSTest/Expecto
  users, and not reversible without a breaking change.
