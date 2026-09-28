# ADR-024 — Projections are declared: versioned rebuilds, and effects that start at the head

## Status

Accepted (2026-09-28) — extends [ADR-009](adr-009-projections-as-dumb-handlers.md)
(handlers stay dumb; the kernel learns which ones may be replayed). Both kernels, both
feeds.

## Context

ADR-009 gives every consumer one interface, `IChangeHandler` (and `IEventHandler`):
a name and `HandleAsync`. Two kinds of handler hide behind it:

- **Projections** — derive state from the feed into a table, an index, a cache. Replaying
  them is safe and is how they are built and repaired: a new one backfills from the start,
  a reset rebuilds it.
- **Effects** — send mails, call webhooks, publish to a bus, translate into events.
  Replaying them repeats the effect.

The kernel cannot tell them apart, and three things go wrong because of that (aksara
feedback F-16):

1. **Upgrades that require a rebuild are manual.** The 2.0.0 note said "reset every
   handler whose output must be complete". Only the application knows which those are;
   aksara kept the list in a status document and built its own one-shot rebuild (a
   migration row, a schema contributor resetting checkpoints under a lock).
2. **A breaking projection change needs a new handler name.** The projection-schema
   recipe's route — new name, new checkpoint at 0 — leaves the old checkpoint and failure
   rows behind and works against "`Name` is identity, never rename it".
3. **A new effect handler replays history.** Registration inserts every new handler at
   `seq` 0. A mail handler added to a system with years of orders sends mail for all of
   them on its first start. Tools can only warn: the MCP reset tool's description says
   "ONLY for projections".

## Decision

### 1. `IProjection` declares a replayable handler

```csharp
public interface IProjection
{
    int Version { get; }                      // bump on a breaking change → rebuilt once
    Task ResetAsync(CancellationToken ct);    // empty this projection's own target
}
```

A projection class implements it next to `IChangeHandler` or `IEventHandler`; the
interface is feed-agnostic and lives in `Papuma.Kernel.Core`, so both kernels read it.
`ResetAsync` must be idempotent (`DELETE`, `TRUNCATE`, dropping an index) — the kernel may
call it again after a crash. Handlers without the interface are effects.

### 2. The checkpoint remembers the projection version

`papuma.checkpoint` gains `projection_version int` (`NULL` for effects). Before the
first delivery, each processor reconciles every projection under a waiting row lock
(`FOR UPDATE`, not `SKIP LOCKED` — at most one cycle of another instance to wait for):

| stored | declared | action |
|---|---|---|
| no row | *v* | insert at the beginning with version *v* — it backfills itself, as today |
| `NULL` | *v* | record *v*, **no rebuild** — the first start with this feature must not rebuild every projection |
| < *v* | *v* | **rebuild**: `ResetAsync()`, then reset the cursor and clear its failures, record *v* |
| = *v* | *v* | nothing |
| > *v* | *v* | **pause** this projection in this instance: it runs older code during a rolling deploy and must not write the old shape into the rebuilt target. Checked again each cycle; logged once. The version never goes down. |

`ResetAsync` commits in its own transaction before the cursor reset commits (on SQLite
it runs with no transaction open at all: it may write to the same file, and the file has
one write lock). A crash in between leaves the old version stored, so the next start
repeats both — hence idempotency. For a table under row-level security, `TRUNCATE` is
the reset: it is not subject to RLS (the role needs the `TRUNCATE` privilege). Two new instances starting together serialize on the row lock; the second
finds the version already recorded.

### 3. Resetting projections is one call

`ResetProjectionsAsync()` on each processor calls `ResetAsync()` and resets the cursor
for every projection it runs — the one line an upgrade note like 2.0.0's needs;
`ResetProjectionAsync(name)` does it for one and refuses a handler that is not a
projection.
`ResetCheckpointAsync(name)` stays as the raw tool: it resets the cursor only.

### 4. Effects can start at the head

`[StartsAtFeedHead]` on a handler class makes its first registration start at the head —
everything committed before counts as delivered — instead of the beginning. It is an
opt-in, so no handler changes behaviour on upgrade; the docs recommend it for every
effect. It has no effect on an existing checkpoint, and combining it with `IProjection` is
rejected at registration (a projection that skips history is incomplete by
construction). An attribute rather than an interface: it declares, it does nothing.

### 5. Tools know the difference

The dashboard and the MCP lag tool show each handler's kind (projection with version,
paused, effect). The MCP tool `reset_feed_checkpoint` runs `ResetProjectionAsync`: it
empties the projection's target before the replay, and it refuses effects — the rule
it only stated becomes enforced.

## Consequences

- **Positive:** A breaking projection change is "bump `Version`", with a stable name and
  no leftover checkpoint rows. An upgrade that requires a rebuild is
  `ResetProjectionsAsync()`.
- **Positive:** Rolling deploys are safe: an old instance cannot overwrite a rebuilt
  projection, and a new one rebuilds exactly once however many start.
- **Positive:** A new effect handler can be added to a system with history without
  replaying it — and the reset tools stop offering to repeat effects.
- **Negative:** A version bump empties the target: readers see it incomplete until the
  replay catches up. Where that is not acceptable, the recipe's route stays — a new table
  under a new name, switch reads when its lag is 0, then drop the old one. The recipe
  describes both and when to pick which.
- **Negative:** Two more things to know when writing a handler (`IProjection`,
  `[StartsAtFeedHead]`). Unmarked handlers behave exactly as today.
- **Neutral:** The default start stays "the beginning" for every handler in 2.x; making
  "the head" the default for effects is a candidate for 3.0, once handlers are marked.

## Alternatives considered

- **`bool IsResettable` on `IChangeHandler`.** Needs a default interface member on the
  core interface and says nothing about *how* to reset; `IProjection` carries the version
  and the reset hook where they belong.
- **The kernel truncates the target itself.** It does not know the target — a table, a
  search index, a file — and ADR-009 keeps it that way.
- **Version bump rebuilds into a shadow table and swaps.** Zero downtime, but the kernel
  would have to own the target's DDL. The new-name route gives the same result under the
  application's control.
- **Effects start at the head by default.** Safer for new effect handlers, but a silent
  change for today's unmarked projections: one registered after the upgrade would no
  longer backfill. Deferred to a major version.
- **A timestamp or migration table instead of a version number.** A number is the
  smallest thing that orders deploys; everything else is the application's migration
  tool's business.

## Related

- [ADR-009](adr-009-projections-as-dumb-handlers.md) — dumb handlers; this ADR keeps them
  dumb and only declares their kind.
- [ADR-022](adr-022-snapshot-cursor.md) — the cursor that "reset" and "start at the head"
  set.
- Recipe [projection-schema.md](../recipes/projection-schema.md) — breaking changes: version
  bump or new name.
- Feedback F-16 (aksara), `docs/feedback/papuma-feedback.md`.
