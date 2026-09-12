# ADR-008: Rollback is an update with metadata, not its own operation type

## Status

Accepted (2026-06-11)

## Context

The idea "events: insert / update / delete / rollback" suggested itself. But: a
rollback to version n is technically a marker, while in domain terms it is simply
a state change. Projections only care about the state transition — a fourth
operation type would force every handler to handle a special case that is not
one.

## Decision

1. `ChangeOperation` stays **Insert / Update / Delete**.
2. Rollback is a kernel API that reconstructs the target state (applying diffs
   backwards, ADR-004) and stores it as a **normal update** — with metadata:

   ```csharp
   await session.RollbackAsync<User>(id, toVersion: 3, expectedVersion: 7);
   ```

   produces a ChangeRecord with `version = 8` and

   ```json
   { "isRollback": true, "restoredVersion": 3 }
   ```

3. The version history is **append-only**: a rollback deletes no changes, it adds
   one. Version 8 has the same content as version 3 — the log stays gapless and
   auditable.
4. Handlers that want to treat rollbacks specially read `metadata.isRollback` —
   but they don't have to.

## Consequences

- Projections stay dumb: three operations, done.
- The audit trail stays complete: who rolled back what to where and when is in the
  feed.
- A rollback across schema versions runs through the upcaster pipeline (ADR-005)
  before being stored — the reconstructed state is always written in the current
  schema.
- Policy-redacted fields (ADR-007) are not reconstructible from diffs; a rollback
  either restores such fields from the referenced storage location or fails typed
  — silently wrong values do not exist.
