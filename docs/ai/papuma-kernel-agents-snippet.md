# AGENTS.md snippet for applications using Papuma.Kernel or Papuma.Kernel.Local

Copy the block for whichever kernel your application uses into `AGENTS.md` /
`CLAUDE.md` and adjust the placeholders. It gives agents the mental model and
the non-negotiable rules — details live in the
[playbook](papuma-kernel-playbook.md) and the package docs. The two blocks
share almost all of their rules (same model, same `Papuma.Kernel.Core`); only
the persistence-specific lines differ.

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
   mechanism.
10. Never write directly into `papuma.*` tables or alter their schema.

### Placeholders (adjust)

- Scope resolution: <HOW THIS APP DETERMINES THE TENANT, e.g. subdomain/claim>
- Registered document types: <LIST OR POINTER TO THE MODEL BOOTSTRAP FILE>
- Projections vs. effect handlers: <WHICH HANDLERS MAY BE RESET>

### Reference

- Playbook: docs/ai/papuma-kernel-playbook.md in the Papuma.Kernel repo
  (also shipped inside the NuGet package under docs/)
- Concepts (the why): docs/vNEXT/concepts.md §1–§20
- GDPR tooling: docs/vNEXT/gdpr.md · Diagnostics: docs/vNEXT/observability.md
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

- Playbook: docs/ai/papuma-kernel-playbook.md, "Differences when using
  Papuma.Kernel.Local" section (also shipped inside the NuGet package under
  docs/)
- Design rationale (what's shared vs. deliberately different per engine):
  docs/analyses/local-kernel-sqlite-sibling.md in the Papuma.Kernel repo
- Concepts (the why, applies to the shared surface of both kernels):
  docs/vNEXT/concepts.md §1–§20
- GDPR tooling: docs/vNEXT/gdpr.md
```
