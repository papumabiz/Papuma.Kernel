# ADR-003: Atomic write path with optimistic concurrency and RETURNING OLD/NEW

## Status

Accepted (2026-06-11)

## Context

The naive flow "load old document → diff → write" has two problems:

1. **Race condition**: between loading and writing, another writer can change the
   document — the diff would then be computed against a stale state.
2. **Two roundtrips** per save.

PostgreSQL 18 introduces `RETURNING OLD/NEW`: a single `UPDATE`/`DELETE` delivers
the state before and after the change atomically.

## Decision

1. **Optimistic concurrency is a kernel invariant.** Every document carries a
   `version` (bigint, starting at 1). Updates and deletes require the expected
   version; a hit of 0 rows throws `ConcurrencyException`.
2. **The write path is a single statement** per document:

   ```sql
   UPDATE papuma.document
   SET data = @data, version = version + 1, schema_version = @schemaVersion, updated_at = now()
   WHERE tenant_id = @tenantId AND document_type = @type AND id = @id
     AND version = @expectedVersion
   RETURNING old.data AS old_data, new.data AS new_data, new.version;
   ```

   Insert: `INSERT ... RETURNING new.version` (old is NULL).
   Delete: `DELETE ... WHERE ... AND version = @expectedVersion RETURNING old.data`.

3. **Diff and ChangeRecord are created in the same transaction**: the kernel diffs
   `old_data`/`new_data` in C#, applies policies (ADR-007) and writes the
   `ChangeRecord` before commit. Document and change are never inconsistent.
4. The API makes the expected version explicit:

   ```csharp
   SaveResult<T> Save<T>(T document, long expectedVersion);   // 0 = insert expected
   ```

   A "last-writer-wins" mode deliberately does not exist.

## Consequences

- No time window between reading and writing; the diff is guaranteed to be
  computed against the state that was actually replaced.
- Concurrency conflicts are a normal, typed failure case that applications must
  handle (reload + retry, or surface the error to the caller).
- **Detection is framework, resolution is application.** This fully covers the
  multi-user case: two users load version 5; the second save with
  `expectedVersion: 5` fails — lost updates are ruled out. The `version` is
  threaded through the application for this (frontend field, ETag, API response).
  The version uniquely identifies the document content; a separate content
  comparison would be redundant.
- The `ConcurrencyException` carries the expected and the actual version. With
  that, the application can load the intermediate diffs via the change feed
  (ADR-004) and build precise conflict UIs ("field X was changed in the
  meantime") or merge itself for disjoint field sets — automatic merging is
  deliberately not a kernel function (a domain decision). For low-conflict
  multi-user edits on different fields, patch is the fitting primitive (ADR-012).
- The `version` in the ChangeRecord is gapless per document (the unique index
  `(tenant, type, id, version)` additionally enforces this).
- **Re-creation after delete continues the version count** (phase-2 ruling): an
  insert starts at `max(change.version) + 1` of the same document id, not at 1 —
  otherwise the reused id would collide with the gapless change history.
  `expectedVersion: 0` keeps the semantics "document does not exist".
- Binding to PostgreSQL ≥ 18 (ADR-001) — intended.
