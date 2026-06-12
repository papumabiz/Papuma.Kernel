# AGENTS.md snippet for applications using Papuma.Kernel

Copy the block below into your application's `AGENTS.md` / `CLAUDE.md` and adjust
the placeholders. It gives agents the mental model and the non-negotiable rules —
details live in the [playbook](papuma-kernel-playbook.md) and the package docs.

---

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
5. Reading: `LoadAsync`/`LoadByKeyAsync` (immediately consistent), declared keys
   for lookups, SQL views only with `security_invoker = on`. Do not invent a
   query DSL.
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
