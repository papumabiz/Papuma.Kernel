# ADR-012: Partial updates as a patch primitive

## Status

Accepted (2026-06-11)

## Context

The standard write path (ADR-003) is load → modify → save with a full document
replace. Frequent cases, however, change only single fields (display name,
password, counters). A full cycle for that means: one read roundtrip,
serialization of the whole document, and artificial concurrency conflicts between
writers that do not even touch the same fields.

## Decision

1. Next to `Save` there is a second write primitive, **`Patch`**:

   ```csharp
   await session.PatchAsync<User>(id, p => p
       .Set(x => x.DisplayName, "Harry")
       .Remove(x => x.Nickname)
       .Increment(x => x.LoginCount));
   ```

2. **No prior load — neither by the caller nor internally.** A patch is a single
   atomic statement; the "read" happens inside the `UPDATE`:

   ```sql
   UPDATE papuma.document
   SET data = jsonb_set(data, '{displayName}', @value),
       version = version + 1,
       updated_at = now()
   WHERE tenant_id = @tenantId AND document_type = 'User' AND id = @id
   RETURNING old.data, new.data, new.version;
   ```

   `RETURNING OLD/NEW` (ADR-001/003) delivers both states without the document
   ever reaching the client. From these, the diff, the policies and the
   ChangeRecord arise exactly as with a full save — in the same transaction.
   0 rows → the document does not exist (`DocumentNotFoundException`).

3. **The operation catalog is deliberately minimal**: `Set`, `Remove`,
   `Increment` on typed property paths. No query DSL, no conditional expressions,
   no array manipulation beyond setting by index — whoever needs more uses load +
   save.

4. **Concurrency is opt-in for patches.**
   - Without `expectedVersion`: deliberate **field-level last-writer-wins** —
     correct for independent fields (two concurrent patches on `displayName` and
     `phone` do not conflict; the version number still serializes them cleanly).
   - With `expectedVersion`: for read-modify-write semantics, when the new value
     depends on previously read state.
   - Dependencies on the *current* value without a version check are expressed by
     the catalog as a SQL expression (`Increment`) — atomic without a conflict
     window.

5. **Validation remains possible without sacrificing the benefit**: a validator
   can be registered per document type; the kernel then deserializes `new.data`
   from the RETURNING and validates **before commit** — on failure, rollback and
   a typed exception. The default is without a validator (patches on declaratively
   "safe" fields).

6. **The version always keeps counting** (`version + 1`); the ChangeRecord is
   indistinguishable from a save ChangeRecord — consumers (ADR-009) do not need
   to know about patches.

## Consequences

- Field changes cost one roundtrip instead of two and do not conflict with
  writers of other fields.
- Diff, policies (e.g. `[TrackHash]` on the password) and feed invariants hold
  unchanged — there is no "policy bypass" via patch.
- Last-writer-wins at field level is a conscious choice of the caller, not
  default kernel behavior (the default `Save` still demands `expectedVersion`).
- Upcasting (ADR-005): a patch on a document with an old `schema_version` fails
  typed when the patched path would be affected by an upcaster — the caller must
  then use load + save (which lifts the schema). Patches on schema-current
  documents are unrestricted.
- The catalog boundary is discipline against ORM drift: as soon as someone
  demands "conditional patches", the answer is load + save, not catalog
  extension.
- **Clarification (2026-06-12):** conditional *write semantics* (invariants like
  "stock never negative") arise sanctioned from **`Increment` + a type
  validator**: the increment computes atomically inside the statement, the
  validator checks the stored result before commit and rejects typed via
  savepoint rollback — the atomic conditional decrement without retry loops
  (concepts §17). That is not a catalog extension but a composition of existing
  primitives.
