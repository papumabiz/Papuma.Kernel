# ADR-020 — Composite keys

## Status

Accepted (2026-09-25) — amends [ADR-006](adr-006-keys-and-constraints.md); amended
2026-09-28 — conditional keys deferred against a trigger (see *Amendment*)

## Context

ADR-006 declares keys on single fields and materializes each as a partial
expression index. A common uniqueness rule does not fit that shape: *unique
within a parent* — ticket number per project, slug per category, SKU per
warehouse, email per organization inside one tenant. The tenant is already part
of every key index; the parent inside the tenant is not.

The documented workaround was to flatten the combination into one extra field
(`Key = $"{ProjectId}/{Number}"`) and declare that field unique. Consumer
feedback (jejak, F-5) used it and named the costs:

- The combination is stored twice. When `ProjectId` changes, the application
  must remember to rewrite `Key`, or uniqueness silently checks stale data.
- The helper field travels through every diff and the change feed without
  domain meaning.
- Every consumer invents its own separator and format.

## Decision

1. `UniqueKey` and `LookupKey` accept an anonymous type of property chains:

   ```csharp
   .Document<Ticket>(d => d.UniqueKey(x => new { x.ProjectId, x.Number }))
   ```

   Component order is the declaration order; it is the index column order and
   the value order for lookups. `(ProjectId, Number)` and `(Number, ProjectId)`
   are different keys. Attributes (`[UniqueKey]`) stay single-field — a
   composite key is always declared fluently.
2. The kernel materializes one partial expression index with one expression per
   component, on PostgreSQL and SQLite alike:

   ```sql
   CREATE UNIQUE INDEX ux_papuma_doc_ticket_projectid__number
       ON papuma.document (scope, tenant_id, (data #>> '{projectId}'), (data #>> '{number}'))
       WHERE document_type = 'Ticket';
   ```

   Components in the index name are joined by `__`, so `(a, b)` cannot collide
   with the nested path `a.b`.
3. **Missing components are not enforced**, exactly like single-field keys: a
   document lacking any component (null or absent) never conflicts. PostgreSQL's
   `NULLS NOT DISTINCT` is deliberately not used — it would give composite keys
   different semantics from single-field keys, and SQLite has no equivalent.
4. Lookup takes one value per component:
   `LoadByKeyAsync<Ticket>(x => new { x.ProjectId, x.Number }, ["p1", 42])`.
   Bulk operations (`PatchWhereAsync`, `DeleteWhereAsync`) and masked reads by key
   stay single-field and reject a composite key with an `ArgumentException`.
5. Public metadata stays compatible. `KeyMetadata.Path` and
   `UniqueKeyViolationException.KeyPath` keep their single-field values; for a
   composite key they hold the component paths comma-separated
   (`projectId,number`). New: `KeyMetadata.Paths`, `IsComposite`,
   `ComponentSegments` and `UniqueKeyViolationException.KeyPaths`.

## Consequences

- **Positive:** "Unique within a parent" is declared where it belongs, on the
  real fields, with no duplicated state and nothing extra in the feed.
- **Positive:** One mechanism for both engines; single-field keys keep their
  paths, index names and behavior unchanged.
- **Negative:** Changing a key declaration leaves the old index in place — the
  kernel creates indexes idempotently but never drops them. Moving from a
  flattened field to a composite key needs a manual `DROP INDEX` of the old one.
- **Negative:** Consumers that parse `KeyPath` as one dot-path must handle the
  comma form, or switch to `KeyPaths`.
- **Neutral:** Bulk operations over a composite key are not offered; select ids
  and use `PatchManyAsync`/`DeleteManyAsync`. Revisit only on repeated demand.

## Amendment (2026-09-28): conditional keys are deferred

aksara (feedback F-19) asked for a filter on keys — "unique among the documents in state
X" (`UniqueKey(x => x.UserId, where: x => x.Role == "owner")`), translated into the
partial index predicate. Not now:

- **Index lifecycle.** The kernel never drops indexes (ADR-006). A changed condition
  gets a new index; the old one stays in force and silently enforces the old rule. A
  key without a condition does not have this failure mode in practice.
- **Constant fidelity.** The constant must compare exactly as the serializer writes it,
  in two JSON engines (`#>>` text in PostgreSQL, `json_extract` values in SQLite, where
  `true` reads as `1`) — a type matrix for enums, numbers, booleans and dates.
- **Surface.** `LoadByKeyAsync` would have to apply the condition, and the F# facade
  would need it as a quotation.
- **An alternative exists.** A slot document with a derived id — the kernel's primary
  key as the uniqueness, written first in the session — expresses "at most one per …"
  atomically today (concepts §34).

**Trigger to revisit:** two applications report a case the slot document cannot model.
The design then includes a way to find orphaned key indexes (for example a startup
check that reports indexes of undeclared keys), so a changed condition cannot linger.

## Alternatives considered

- **Keep the flattening workaround, documented.** Zero kernel work, but it moves
  an integrity rule into application code that must keep a derived field in sync
  — the failure mode is silent.
- **`NULLS NOT DISTINCT` for composite keys.** Stricter, but inconsistent with
  single-field keys and not portable to SQLite.
- **A params-style declaration** (`UniqueKey(x => x.ProjectId, x => x.Number)`).
  Works, but the anonymous-type form is what .NET developers know from EF Core's
  `HasIndex(x => new { … })`, and it keeps one method per key kind.
