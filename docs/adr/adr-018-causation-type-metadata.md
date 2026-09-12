# ADR-018 — Causation type as a metadata field

## Status

Accepted

## Context

After adding `actor_id` as a first-class column (ADR-017), the change and event
records answer **who** caused a change but not **what** caused it. The existing
`causationId` links to an external command instance (a GUID), but a human
reading the feed still has to look up that id to learn the command *name*.

Adding a `causationType` (e.g. `"ActivateUser"`, `"ImportBatch"`) closes this
gap: the feed becomes self-describing for debugging, process analysis, and
lightweight audit without joining external logs.

### Why metadata, not a column?

| Criterion | Own column | JSONB metadata field |
|-----------|-----------|---------------------|
| Schema migration | `ALTER TABLE` on three tables | None — already in `metadata jsonb` |
| NOT NULL semantics | Awkward — many writes have no named command | Natural — absent means "not provided" |
| Query pattern | `WHERE causation_type = '...'` | `WHERE metadata->>'causationType' = '...'` (GIN-indexable) |
| Consistency | Separate from `causationId` | Next to `causationId` in the same object |
| Feed wire format | Extra column in every reader | Already part of the metadata object |

The metadata column already carries `correlationId`, `actorId`, `causationId`,
and `traceparent`. Adding `causationType` keeps the causation context together
and avoids schema churn for an optional, application-supplied value.

## Decision

1. Add an optional `CausationType` property to `SessionOptions`.
2. When set, write `"causationType": "<value>"` into the JSONB `metadata` of
   every change record and event record produced by the session.
3. No DDL change, no new column, no migration step.

The value is a free-form string chosen by the application (typically the command
class name or a slice identifier). The kernel does not validate or constrain it.

## Consequences

- **Positive:** The feed becomes self-describing — `metadata` now answers who
  (`actorId`), why (`causationId` + `causationType`), and where
  (`correlationId` + `traceparent`).
- **Positive:** Zero migration cost — existing deployments gain the field on
  next write without schema changes.
- **Negative:** The field is optional and untyped — inconsistent naming across
  teams is possible. This is acceptable because the kernel is a library, not a
  framework; naming conventions belong to the application.
- **Neutral:** If query-by-causation-type becomes a hot path, a GIN index on
  `metadata` or a generated column can be added later without breaking the API.
