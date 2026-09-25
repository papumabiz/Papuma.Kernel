# AGENTS.md snippet for applications using Papuma.Kernel or Papuma.Kernel.Local

Copy the block for whichever kernel your application uses into `AGENTS.md` /
`CLAUDE.md` and adjust the placeholders. It gives agents the mental model and
the non-negotiable rules — details live in the
[playbook](papuma-kernel-playbook.md) and the package docs. The two blocks
share almost all of their rules (same model, same `Papuma.Kernel.Core`); only
the persistence-specific lines differ. Using `Papuma.Kernel.FSharp` on top of
either kernel? Also copy the F# addendum below.

---

## For apps using `Papuma.Kernel` (PostgreSQL ≥ 18)

```markdown
## Persistence: Papuma.Kernel (document-sourced CQRS, PostgreSQL ≥ 18)

This application stores state as JSON documents via Papuma.Kernel.
The document is the truth; a reversible change feed and an event log are
derived automatically. NOT event sourcing, NOT an ORM, NO query DSL.

### Rules (binding, from the package's ADRs)

1. Write only through `DocumentSession` (unit of work): `store.OpenSession(scope)`
   → writes → `CommitAsync()`. Without commit, everything is discarded.
2. `SaveAsync` ALWAYS with `expectedVersion` (`0` = insert). Handle
   `ConcurrencyException`: reload, re-decide — no blind retries.
3. Single fields: `PatchAsync` (no load needed). Bounded counters:
   `Increment` + a registered validator — never load-check-save loops.
4. Personal fields ARE UNDER A POLICY before they are ever stored:
   `[SensitiveData]` / `[TrackHash]` / `[DoNotTrack]` or a fluent override.
5. Reading has a default order, not a free menu: one current document →
   `LoadAsync`/`LoadByKeyAsync` (declared keys, immediately consistent); anything
   derived — list, join, aggregation, search, external — → an `IChangeHandler`
   projection, **THE DEFAULT**. A SQL view over `papuma.*` is the exception, not a
   peer choice: only ad-hoc/reporting/BI, read-only, `security_invoker = on`,
   schema-stable, non-sensitive fields. "Real-time / no lag" alone is not a reason
   (a single current read is already strong-consistent via `LoadByKeyAsync`). Never
   invent a query DSL.
6. Reacting: `IChangeHandler`/`IEventHandler`. Handlers are IDEMPOTENT
   (at-least-once) and never block (no waiting for humans — write a task
   document instead). `Name` is the checkpoint identity: never rename it.
7. Schema evolution: make changes additive when possible; register
   renames/restructurings as `Upcast(fromVersion, …)`. Never simulate a rename
   additively.
8. Events (`AppendAsync`) only for facts without state truth (login, email
   sent). State transitions belong in the document.
9. Multi-tenancy: every session is scope-bound (`ScopeContext.Tenant(id)` /
   `Platform`). Never mix data across scopes; workers use the 'All'-scope
   mechanism. Own tables in the same database (projection targets) get RLS via
   `papuma.scope_visible`/`papuma.scope_writable` — never copy the setting names.
10. Never write directly into `papuma.*` tables or alter their schema.
11. Integration tests: `Papuma.Kernel.Testing` — stores and processors on
    `AppDataSource` (non-superuser, RLS applies), `processor.DrainAsync()` to run
    handlers; a fresh tenant per test.

### Placeholders (adjust)

- Scope resolution: <HOW THIS APP DETERMINES THE TENANT, e.g. subdomain/claim>
- Registered document types: <LIST OR POINTER TO THE MODEL BOOTSTRAP FILE>
- Projections vs. effect handlers: <WHICH HANDLERS MAY BE RESET>

### Reference

Read the docs of the version this app builds against, not copies or the web:
`~/.nuget/packages/papuma.kernel/<version>/docs/` (Windows: `%USERPROFILE%\.nuget\packages\...`;
`$NUGET_PACKAGES` overrides the root; `<version>` is the PackageReference in the project file).

- Playbook: docs/ai/papuma-kernel-playbook.md
- Concepts (the why): docs/concepts.md
- GDPR tooling: docs/gdpr.md · Diagnostics: docs/observability.md
```

---

## For apps using `Papuma.Kernel.Local` (SQLite, embedded, no server)

```markdown
## Persistence: Papuma.Kernel.Local (document-sourced CQRS, SQLite, embedded)

This application stores state as JSON documents via Papuma.Kernel.Local, a
single-writer SQLite sibling of Papuma.Kernel — same model
(`Papuma.Kernel.Core`), same API shape, independent storage implementation.
The document is the truth; a reversible change feed and an event log are
derived automatically. NOT event sourcing, NOT an ORM, NO query DSL.

### Rules (binding — same source as Papuma.Kernel's ADRs, SQLite specifics noted)

1. Write only through `SqliteDocumentSession` (unit of work):
   `store.OpenSession(scope)` → writes → `CommitAsync()`. Without commit,
   everything is discarded.
2. `SaveAsync` ALWAYS with `expectedVersion` (`0` = insert). Handle
   `ConcurrencyException`: reload, re-decide — no blind retries.
3. Single fields: `PatchAsync` (no load needed). Bounded counters:
   `Increment` + a registered validator — never load-check-save loops.
4. Personal fields ARE UNDER A POLICY before they are ever stored:
   `[SensitiveData]` / `[TrackHash]` / `[DoNotTrack]` or a fluent override.
5. Reading has a default order, not a free menu: one current document →
   `LoadAsync`/`LoadByKeyAsync` (declared keys, immediately consistent); anything
   derived — list, join, aggregation, search, external — → an `IChangeHandler`
   projection, **ALWAYS** (there is no SQL-view read lens on SQLite — that
   Postgres exception doesn't apply here). Never invent a query DSL.
6. Reacting: `IChangeHandler`/`IEventHandler`. Handlers are IDEMPOTENT
   (at-least-once) and never block (no waiting for humans — write a task
   document instead). `Name` is the checkpoint identity: never rename it.
7. Schema evolution: make changes additive when possible; register
   renames/restructurings as `Upcast(fromVersion, …)`. Never simulate a rename
   additively.
8. Events (`AppendAsync`) only for facts without state truth (login, email
   sent). State transitions belong in the document.
9. Multi-tenancy/scope: every session is scope-bound (`ScopeContext.Tenant(id)` /
   `Platform`) via explicit predicates — there is no row-level security on
   SQLite (a single-writer, single-process store has no second tenant's
   process to defend against; still fail-closed on a missing scope).
10. Never write directly into the underlying `document`/`change`/`event`/
    `checkpoint`/`failure` tables or alter their schema.
11. One `SqliteDocumentStore` per database file per process — it's a
    single-writer store. Never open the same `.db` file from two processes.

### Placeholders (adjust)

- DB file location: <WHERE THE .db FILE LIVES, e.g. per-user app-data dir>
- Registered document types: <LIST OR POINTER TO THE MODEL BOOTSTRAP FILE>
- Projections vs. effect handlers: <WHICH HANDLERS MAY BE RESET>

### Reference

Read the docs of the version this app builds against, not copies or the web:
`~/.nuget/packages/papuma.kernel.local/<version>/docs/` (Windows: `%USERPROFILE%\.nuget\packages\...`;
`$NUGET_PACKAGES` overrides the root; `<version>` is the PackageReference in the project file).

- Playbook: docs/ai/papuma-kernel-playbook.md, "Differences when using
  Papuma.Kernel.Local" section — read it first; other pages describe the
  PostgreSQL kernel unless they say otherwise
- Design rationale (what's shared vs. deliberately different per engine):
  docs/analyses/local-kernel-sqlite-sibling.md
- Concepts (the why, applies to the shared surface of both kernels):
  docs/concepts.md
- GDPR tooling: docs/gdpr.md
```

---

## Addendum for F# apps (either block above, plus `Papuma.Kernel.FSharp`)

Append this to whichever block above matches your kernel — the rules don't
change for F#, only the syntax for three of them:

```markdown
### Additions for F# (Papuma.Kernel.FSharp)

- Rule 3 (Patch/Increment): use `SetQ`/`RemoveQ`/`IncrementQ` with a quotation
  (`<@ fun x -> x.Field @>`), not `Set`/`Remove`/`Increment` with a lambda —
  F# has no compiler support for converting `x => x.Field` into an
  `Expression<Func<T,TValue>>`.
- Keys: `d.UniqueKey(fun x -> box x.Email)`; composite keys with a tuple,
  `box (x.ProjectId, x.Number)` — never an anonymous record. Composite
  `LoadByKeyAsync` needs the values typed as `IReadOnlyList<obj>`.
- `trySaveAsync`/`tryPatchAsync` return `Result<'T, KernelError>` for the
  three expected write outcomes instead of throwing — optional, not
  mandatory; the throwing API still works.
- `runSession` replaces a hand-written `try`/`finally` around
  `DisposeAsync` — F#'s `use` doesn't bind `IAsyncDisposable`.
- Rule 4 (privacy policies): additionally, no discriminated-union or
  `option` fields on document types — the kernel's JSON config is fixed on
  purpose. Map `option` to a plain nullable-style field at the boundary
  (`Option.toObj`/`ofObj`/`toNullable`/`ofNullable`, already in `FSharp.Core`).

### Reference

Read the docs of the version this app builds against, not copies or the web:
`~/.nuget/packages/papuma.kernel.fsharp/<version>/docs/` (Windows: `%USERPROFILE%\.nuget\packages\...`;
`$NUGET_PACKAGES` overrides the root; `<version>` is the PackageReference in the project file).

- `Papuma.Kernel.FSharp` package README (package root)
- Playbook: docs/ai/papuma-kernel-playbook.md, "Using Papuma.Kernel.FSharp" section
- Slice conventions in F#: docs/ai/papuma-kernel-slice-conventions.md, "Using Papuma.Kernel.FSharp" section
- Concepts §29 (the why): docs/concepts.md
```
