# ADR-014: Bulk operations as set-based patch

## Status

Accepted (2026-06-11)

## Context

Applications need changes across multiple documents: "archive all users with
status X", "edit this list of ids". PostgreSQL's `RETURNING OLD/NEW` is
set-based — an `UPDATE ... WHERE` delivers **one row per affected document** with
old and new state. The phase-2 spike finding (ADR-003) thus carries over to sets:
one statement, atomic, diff material for every document.

At the same time, the back door to a query DSL looms here, which Papuma Kernel
deliberately does not want to be (ADR-006/009): "update with arbitrary WHERE"
would be the beginning of a LINQ provider.

## Decision

1. **Bulk = patch, never save.** Bulk operations use exclusively the patch
   catalog from ADR-012 (`Set` / `Remove` / `Increment`) — the same patch across
   N documents. A "bulk save" of whole documents does not exist.
2. **Two selection forms, no third:**

   ```csharp
   // a) predicate on declared metamodel keys (ADR-006)
   await session.PatchWhereAsync<User>(
       where: w => w.Key(x => x.Status, "inactive"),
       patch: p => p.Set(x => x.Status, "archived"));

   // b) explicit id list (WHERE id = ANY(@ids))
   await session.PatchManyAsync<User>(ids, p => p.Set(x => x.Status, "archived"));
   ```

   Predicates are limited to **declared keys** (equality, possibly key lists) —
   exactly the fields that carry expression indexes anyway. More complex
   selections are determined by the application itself (projection, SQL), which
   then uses form (b). A free WHERE DSL deliberately does not exist.
3. **Delete analogously**: `DeleteWhereAsync` / `DeleteManyAsync` via
   `DELETE ... RETURNING old.data` — same mechanics, delete diffs per document.
4. **One ChangeRecord per document**, not per statement. Consumers (ADR-009)
   notice nothing special; gapless versioning per document stays intact (every
   hit row bumps its own `version`). A shared `correlationId` in the metadata
   connects the records of one bulk operation; the change inserts happen batched
   in the same transaction.
5. **No `expectedVersion`.** Bulk is state-based by definition: predicate and
   change act on the current state within the same statement — there is no
   read-modify-write window; the operation is internally consistent. Parallel
   optimistic writers subsequently run correctly into the `ConcurrencyException`
   (their expected version was bumped).
6. **Schema guard as in ADR-012**: if the bulk patch hits documents with an
   outdated `schema_version` whose patched paths are affected by upcasting, the
   operation fails typed — no silent corruption of old documents.
7. **Policies and the diff pipeline unchanged**: every affected document goes
   through the same diff creation and policy application as a single patch
   (ADR-007).

## Consequences

- Mass changes are a single roundtrip instead of N — and atomic: either all hits
  including their ChangeRecords, or nothing.
- Key predicates are automatically index-backed (synergy with ADR-006).
- Large hit sets mean a large transaction and a feed surge — batch limiting
  (e.g. chunking id lists) is an application decision and is documented. Very
  long bulk transactions also delay feed progress for all consumers (snapshot
  reading, ADR-010).
- Per-document validation (ADR-012 point 5) is expensive at scale; bulk patches
  on validated types deserialize every hit row — a deliberate trade-off of the
  caller.
- Implemented in phase 5 together with the single patch (shared SQL generation).
