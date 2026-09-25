# ADR-020 — Composite keys

## Status

Accepted (2026-09-25) — amends [ADR-006](adr-006-keys-and-constraints.md)

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

## Alternatives considered

- **Keep the flattening workaround, documented.** Zero kernel work, but it moves
  an integrity rule into application code that must keep a derived field in sync
  — the failure mode is silent.
- **`NULLS NOT DISTINCT` for composite keys.** Stricter, but inconsistent with
  single-field keys and not portable to SQLite.
- **A params-style declaration** (`UniqueKey(x => x.ProjectId, x => x.Number)`).
  Works, but the anonymous-type form is what .NET developers know from EF Core's
  `HasIndex(x => new { … })`, and it keeps one method per key kind.
