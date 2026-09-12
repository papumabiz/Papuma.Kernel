# ADR-017: Actor identity as a first-class column

## Status

Accepted (2026-06-17)

## Context

Every write in Papuma is performed by *someone* — a human user, a system service,
a background handler. The session already carries an optional `ActorId`
(`SessionOptions.ActorId`), and `BuildChangeMetadata` writes it into the JSONB
`metadata` column of `papuma.change` and `papuma.event`. That is sufficient for
tracing individual changes, but it has three structural weaknesses:

1. **Not queryable without a functional index.** "Show me everything user X
   changed" requires `metadata->>'actorId'` — no index, full scan. GDPR subject
   access requests ("which data did we process about this person?") hit the same
   wall.
2. **Not enforceable.** A JSONB field cannot carry a `NOT NULL DEFAULT ''`
   constraint; the kernel cannot guarantee that every record has an actor — or
   even that the key name is spelled consistently.
3. **Not on the document itself.** `papuma.document` has `created_at` /
   `updated_at` but no `created_by` / `updated_by`. The most common audit
   question — "who last touched this document?" — requires joining the change
   feed instead of reading the document row.

The `tenant_id` answers "for which tenant"; the `actor_id` answers "who did it" —
orthogonal dimensions that both deserve first-class column treatment.

## Decision

Add `actor_id text NOT NULL DEFAULT ''` to `papuma.change` and `papuma.event`,
and `created_by text NOT NULL DEFAULT ''` / `updated_by text NOT NULL DEFAULT ''`
to `papuma.document`.

### Column semantics

| Table | Column | Written by | Meaning |
|---|---|---|---|
| `papuma.document` | `created_by` | `INSERT` (save with version 0) | The actor who created the document |
| `papuma.document` | `updated_by` | Every `INSERT` / `UPDATE` / `DELETE` | The actor of the most recent write |
| `papuma.change` | `actor_id` | Every change record insert | The actor who caused this change |
| `papuma.event` | `actor_id` | Every event append | The actor who appended this event |

All four columns default to `''` (empty string) — the kernel does not mandate
that every session carries an actor. An empty actor means "system / anonymous /
not provided"; the application decides whether to enforce non-empty actors at its
boundary (e.g. middleware, command handlers).

### Source of the value

The value comes from `SessionOptions.ActorId`, which already exists. No new API
surface is needed — the same property now writes to both the column and the
metadata JSONB (the latter for backward compatibility and because metadata
carries additional context like `correlationId` and `traceparent`).

### What does NOT change

- The `actorId` key in the JSONB `metadata` column stays — it is part of the
  wire format consumed by polyglot clients and the feed-wire-format spec.
- `ChangeRecord` and `EventRecord` gain an `ActorId` property (read from the
  column); handlers that previously read `Metadata["actorId"]` still work.
- No index is created by default on `actor_id` — the column is there for
  application-specific indexes and ad-hoc queries. A GIN or btree index is
  trivial to add per deployment.

## Consequences

- **GDPR subject access** becomes a simple `WHERE actor_id = @userId` or
  `WHERE created_by = @userId` — no JSONB extraction.
- **Audit on the document** is a single-row read: `updated_by` + `updated_at`
  answer "who changed this last?" without touching the change feed.
- **Schema migration**: existing rows get `''` (the default). No data loss, no
  backfill required. Applications that already set `SessionOptions.ActorId` will
  populate the column from the next write onward.
- **Feed wire format**: polyglot consumers can read `actor_id` as a plain column
  instead of parsing JSONB — simpler and faster.
