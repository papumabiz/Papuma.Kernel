# ADR-002: Document as source of truth, change feed derived

## Status

Accepted (2026-06-11)

## Context

v1 used relational tables plus an outbox: changes had to be formulated explicitly
as events, change detection was manual labor. Event sourcing (à la Marten) solves
that, but enforces "the event is the truth" with all its follow-up costs (replay
obligation, GDPR conflict with immutable streams, domain event modeling from day
one).

The actual goal of Papuma is: **changes as a first-class concept without an
event-sourcing mandate.**

## Decision

1. The truth is the **JSON document** of an aggregate (a C# class, serialized to
   JSONB).
2. The **change feed is derived**: on every save, the kernel automatically
   produces a `ChangeRecord` (operation, version, diff, metadata) — in the same
   transaction as the document update.
3. There is **exactly one technical change type** (`DocumentChanged` with
   insert/update/delete as the operation), no domain event types in storage
   (ADR-011).
4. Reading the current state is a plain document load — no replay required.

## Consequences

- Change detection is trivial and complete: a diff of two JSON documents, never
  again a forgotten outbox entry.
- The state is never "derived wrong" — there is no drift between events and state.
- Historical states are reconstructible via the diff log (ADR-004), but that is an
  audit/replay feature, not a loading prerequisite.
- The loss of relational constraints is not ignored but brought back under control
  (ADR-006).
- Model semantics (types, attributes) are available to the kernel — the basis of
  the privacy policies (ADR-007).
