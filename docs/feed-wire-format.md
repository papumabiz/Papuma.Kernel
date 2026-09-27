# Papuma Kernel — Feed Wire Format

Status: verified against `SchemaDdl.cs` and the diff engine (2026-09-27; consumption
rule 1 changed after 1.4 — ADR-022)

> **PostgreSQL kernel only.** `Papuma.Kernel.Local` stores its feed in an embedded
> SQLite file that no second process reads; this contract does not apply to it —
> see the playbook's [Differences section](ai/papuma-kernel-playbook.md#differences-when-using-papumakernellocal-sqlite-embedded).

This is the contract for consuming the change feed and event log from **any
language** (concepts §21). The feed is two ordinary PostgreSQL tables; this
document is everything you need to write a correct consumer without the .NET
library. Runnable references: [samples/polyglot-consumers](https://github.com/papumabiz/Papuma.Kernel/blob/master/samples/polyglot-consumers/README.md).

The kernel guarantees this **format** is stable — tables, columns, diff encoding,
the snapshot-cursor read. The field paths *inside* a diff are the application's document
model and change with it (concepts §21): read the raw feed from within the
application that owns the model; across team or system boundaries, publish explicit
integration events instead.

## 1. The change feed table: `papuma.change`

| Column | Type | Meaning |
|---|---|---|
| `seq` | `bigint` (identity, PK) | Identity of the change; order within a slice. **Not** a cursor (section 4). |
| `scope` | `text` | `'Platform'` or `'Tenant'`. |
| `tenant_id` | `text` | Tenant id; empty string for platform scope. |
| `document_type` | `text` | Logical aggregate name (the CLR type name). |
| `document_id` | `text` | Document identifier. |
| `version` | `bigint` | Document version *after* this change (gapless per document). |
| `schema_version` | `int` | Schema version of the diff content (ADR-005). |
| `operation` | `smallint` | `1` = Insert, `2` = Update, `3` = Delete. |
| `diff` | `jsonb` | The reversible field diff (section 3). |
| `metadata` | `jsonb` | `correlationId`, optional `actorId`/`causationId`/`traceparent`, rollback markers. |
| `occurred_at` | `timestamptz` | When the change was recorded. |
| `txid` | `xid8` | Writer's transaction id — what the snapshot cursor reads by (section 4). |

## 2. The event log table: `papuma.event`

| Column | Type | Meaning |
|---|---|---|
| `seq` | `bigint` (identity, PK) | Identity of the event (separate from the change feed); order within a slice. |
| `scope`, `tenant_id` | `text` | As above. |
| `event_type` | `text` | Logical event name. |
| `payload` | `jsonb` | The policy-applied fact (no `operation`/`diff`; ADR-013). |
| `metadata` | `jsonb` | As above. |
| `occurred_at` | `timestamptz` | When the event was recorded. |
| `txid` | `xid8` | As above. |

Same consumption rules as the change feed; only the row shape differs.

## 3. The diff format (`papuma.change.diff`)

A JSON object keyed by dot-separated field path. Each entry's shape encodes both
the change and the field's privacy policy (ADR-004/007):

```jsonc
{
  "name":          { "old": "Harry", "new": "Harald" }, // tracked: value changed
  "email":         { "new": "x@y.de" },                 // tracked: field added (no "old")
  "nickname":      { "old": "H" },                       // tracked: field removed (no "new")
  "iban":          { "changed": true },                  // Redact policy — value withheld
  "passwordHash":  { "changed": true, "hash": "9f86d…" },// Hash policy — SHA-256 of the value
  "ssn":           { "ref": "User/123/ssn" }             // Reference policy — pointer, no value
}
```

Rules:
- **Presence of `old`/`new` keys encodes field existence**; the *value* may
  legitimately be JSON `null` (null ≠ absent — what makes diffs reversible).
- **Arrays are atomic**: a changed array appears as one entry with the full old
  and new array, never per-index.
- **Insert** diffs have no `old` side; **Delete** diffs have no `new` side
  (subject to policies — a deleted sensitive field is still `{"changed": true}`).
- A field with the `DoNotTrack` policy never appears at all.

"Which fields changed" is simply the set of top-level keys. Values of
policy-protected fields are **not present** — the feed is minimized at write
time, so a foreign-language consumer cannot reach them.

## 4. Consuming correctly — the three mandatory rules

1. **Snapshot cursor (ADR-022).** Sequence numbers are assigned at INSERT but
   become visible at COMMIT, so commit order crosses `seq` order — and no rule
   over a `seq` checkpoint can be safe, because it would need the sequence
   numbers of transactions that are still open. **Up to 1.4 this document
   prescribed `seq > :checkpoint AND txid < pg_snapshot_xmin(pg_current_snapshot())`;
   that predicate loses rows of interleaved multi-write transactions. Switch.**

   The position is a transaction snapshot. Keep it in `papuma.checkpoint` (pick a
   unique `handler_name`; the kernel's own handlers use the same columns):

   | Column | Meaning |
   |---|---|
   | `done_snapshot pg_snapshot` | every transaction visible in it is delivered; `NULL` = see `base_seq` |
   | `base_seq bigint` | while `done_snapshot` is `NULL`: rows with `seq <= base_seq` are delivered (`0` = from the start) |
   | `slice_snapshot pg_snapshot` | the slice in progress; `NULL` = none |
   | `slice_seq bigint` | inside the slice: rows with `seq <= slice_seq` are delivered |
   | `last_seq bigint` | highest `seq` delivered — informational |

   One cycle, in **one transaction** (READ COMMITTED, the default):

   ```sql
   -- a. Lock your row (one consumer per handler_name). New consumer: insert it
   --    first — base_seq 0 replays everything; done_snapshot = pg_current_snapshot()
   --    starts at the head.
   INSERT INTO papuma.checkpoint (handler_name) VALUES (:name) ON CONFLICT DO NOTHING;
   SELECT base_seq, done_snapshot::text, slice_snapshot::text, slice_seq
   FROM papuma.checkpoint WHERE handler_name = :name FOR UPDATE;

   -- b. No slice in progress: take one. Never build a snapshot client-side.
   --    While done_snapshot is NULL:
   SELECT pg_current_snapshot()::text;                -- :slice, and :slice_seq = 0
   --    Otherwise also find the slice's lowest seq, once, through the txid index
   --    (MATERIALIZED keeps min() from becoming a PK scan over the whole table):
   WITH s AS MATERIALIZED (SELECT pg_current_snapshot() AS slice),
        r AS MATERIALIZED (
            SELECT c.seq FROM papuma.change c, s
            WHERE c.txid >= pg_snapshot_xmax(:done::pg_snapshot)
              AND pg_visible_in_snapshot(c.txid, s.slice)
            UNION ALL
            SELECT c.seq FROM papuma.change c, s
            WHERE c.txid = ANY (ARRAY(SELECT pg_snapshot_xip(:done::pg_snapshot)))
              AND pg_visible_in_snapshot(c.txid, s.slice))
   SELECT (SELECT slice::text FROM s), (SELECT min(seq) FROM r);
   --    -> :slice, and :slice_seq = min - 1. min NULL = nothing new: end the cycle.

   -- c. The next batch of the slice — a forward PK range scan. While done_snapshot is NULL:
   SELECT seq, document_type, document_id, version, operation, diff, metadata
   FROM papuma.change
   WHERE seq > greatest(:base_seq, :slice_seq)
     AND pg_visible_in_snapshot(txid, :slice::pg_snapshot)
   ORDER BY seq
   LIMIT :batch;

   --    Otherwise:
   SELECT seq, document_type, document_id, version, operation, diff, metadata
   FROM papuma.change
   WHERE seq > :slice_seq
     AND pg_visible_in_snapshot(txid, :slice::pg_snapshot)
     AND NOT pg_visible_in_snapshot(txid, :done::pg_snapshot)
   ORDER BY seq
   LIMIT :batch;

   -- d. Process the rows in order, then persist and commit. A batch shorter than
   --    :batch completes the slice:
   UPDATE papuma.checkpoint
   SET done_snapshot = :slice::pg_snapshot, slice_snapshot = NULL, slice_seq = 0,
       last_seq = greatest(last_seq, :highest), updated_at = now()
   WHERE handler_name = :name;
   --    a full batch only moves the position inside it:
   UPDATE papuma.checkpoint
   SET slice_snapshot = :slice::pg_snapshot, slice_seq = :last_processed_seq,
       last_seq = greatest(last_seq, :highest), updated_at = now()
   WHERE handler_name = :name;
   ```

   The set of rows in a slice is fixed when it is taken (all its transactions have
   committed), so paginating it by `seq` neither skips nor repeats. Do not add `txid`
   bounds to the batch query: the planner would read the batch through the `txid`
   index, which re-reads and re-sorts the whole slice for every batch. **Ordering you
   get:** causal order across transactions (concurrent ones unordered), `seq` order
   within a slice, strictly
   by `version` per document. A lower `seq` can arrive after a higher one — never
   use `seq` as a "skip everything below" watermark.

2. **Scope (RLS).** All feed tables enforce row-level security. Inside the read
   transaction, set the scope GUC, or you get **empty reads** (fail-closed):

   ```sql
   SELECT set_config('app.current_scope', 'All', true);     -- cross-tenant consumer
   -- or, for one tenant:
   SELECT set_config('app.current_scope', 'Tenant', true),
          set_config('app.current_tenant', :tenant, true);
   ```

   `set_config(..., true)` is transaction-local — it must run in the same
   transaction as the read.

3. **Idempotency.** Advance the cursor *after* processing, in the same
   transaction (step d). Delivery is
   **at-least-once**: a crash before commit re-delivers the batch, so processing
   must be idempotent.

## 5. NOTIFY and schema evolution

- **Wakeup:** `LISTEN papuma_changes` for near-realtime latency; a missed signal
  costs at most one poll interval. Polling is the source of truth, NOTIFY is only
  the alarm clock.
- **Stored shape, not upcast:** upcasting (ADR-005) runs in the .NET kernel, not
  in SQL. Old rows carry their historical `schema_version` and shape. A foreign
  consumer either handles historical shapes or, in practice, relies on the
  additive-change rule (new optional fields, removed fields) that keeps old rows
  readable. There is no SQL-side upcasting.

## 6. Writing

Foreign consumers are **consumers**. Writes go through the kernel's session
(diff, policies, version chain, NOTIFY all arise there) — never `INSERT` into
`papuma.document`/`change`/`event` directly. A foreign app that must write calls
the Papuma application's API.
