# ADR-019 — Scope predicates for application tables

## Status

Accepted (2026-09-25)

## Context

Tenant isolation in the kernel has two layers (concepts §23): explicit scope
predicates in every query, and PostgreSQL row-level security driven by
transaction-local settings (`app.current_scope`, `app.current_tenant`) that
`SetScopeAsync` / `SetAllScopesAsync` write. The second layer fails closed — a
forgotten scope yields empty reads, not another tenant's rows.

That guarantee ends at `papuma.*`. Projections are the *default* derived read
(ADR-009, concepts §16), and the most common target is a table in the same
database. Those tables have no RLS, so every consumer rebuilds isolation by
hand, and a single missing `WHERE tenant_id = …` leaks data. Consumer feedback
(jejak, F-6) hit exactly this: its read tables "sit outside Papuma's RLS and must
re-establish isolation by hand".

The mechanics to close the gap already exist and are public: `SetScopeAsync`
and `SetAllScopesAsync` are extension methods on `NpgsqlConnection`, and the SQL
views of concepts §16 only work because callers set the same settings. What is
missing is a *stated contract* — today a consumer who writes
`current_setting('app.current_tenant', true)` into its own policy depends on an
internal name, and a rename would not break loudly: the policy would silently
match nothing.

## Decision

1. The kernel schema provides two SQL functions, created idempotently by
   `EnsureSchemaAsync`:

   ```sql
   papuma.scope_visible(row_scope text, row_tenant_id text)  RETURNS boolean  -- for USING
   papuma.scope_writable(row_scope text, row_tenant_id text) RETURNS boolean  -- for WITH CHECK
   ```

   They decide exactly like the kernel's own policies: `Tenant` sees and writes
   its own rows, `Platform` its platform rows, `All` (`SetAllScopesAsync`) reads
   everything and writes nothing, and a missing scope yields `false`.
2. **The functions are the public contract, not the settings.** Application
   policies call the functions; the GUC names and values stay an implementation
   detail the kernel may change, as long as the functions keep their meaning.
3. Rows carry the kernel's scope shape: `scope` (`'Tenant'` / `'Platform'`) and
   `tenant_id` (empty string for platform rows) — taken from
   `ChangeRecord.Scope` when projecting.
4. The scope is set with `SetScopeAsync` inside the application's own
   transaction. A projection handler sets **the scope of the change it
   handles**, per change — not `All`, which cannot write by design.
5. The functions are `LANGUAGE sql STABLE` without `SECURITY DEFINER`, so the
   planner inlines them into policies; a test asserts they decide exactly like
   the kernel's policies. The kernel's own policies keep their inline
   expressions for now.

The recipe [read models in the same database](../recipes/same-database-read-models.md)
shows the table, the policy and the handler.

## Consequences

- **Positive:** The fail-closed guarantee extends to the default read path:
  an application table guarded by the two functions leaks nothing when a query
  forgets its tenant filter.
- **Positive:** One definition of visibility. Consumers no longer copy GUC
  names, and the kernel can evolve its scope mechanics without breaking them.
- **Positive:** `WITH CHECK` via `scope_writable` stops a buggy handler from
  writing a row into a tenant other than the change's own.
- **Negative:** One transaction per change in a projection handler, plus a
  `set_config` round trip. For high-volume projections, batching changes of
  the same scope into one transaction is left to the handler.
- **Negative:** RLS protects only against roles it applies to. Superusers and
  roles with `BYPASSRLS` skip it; table owners skip it unless the table has
  `FORCE ROW LEVEL SECURITY`. The recipe states both, but the kernel cannot
  enforce them for application tables.
- **Negative:** The functions are now API. Changing their signature or meaning
  is a breaking change with a major version.
- **Neutral:** Rewriting the kernel's own policies on top of the functions
  would remove the duplicated expressions; deferred until a benchmark shows the
  inlined form performs identically on the hot paths. Until then the
  equivalence test guards against drift.
- **Neutral:** `Papuma.Kernel.Local` (SQLite) has no RLS and no counterpart;
  there, isolation stays explicit predicates only.

## Alternatives considered

- **Document the GUC names as the contract.** Works today with no code, but
  freezes an internal detail, and every consumer policy repeats the full
  three-branch expression — the one place isolation logic must not drift.
- **Tenant column plus mandatory filter in the application** (the pattern of
  the external-read-models recipe). Right for systems that know nothing about
  RLS; for tables in the same PostgreSQL database it gives up the second layer
  the kernel advertises — one missing `WHERE` leaks.
- **Kernel-managed projection tables.** Would need a DDL/DSL for read models,
  which ADR-009 rules out: projections stay dumb handlers owned by the
  application.
