# Papuma vNEXT — Feed Wire Format

Status: verified against `SchemaDdl.cs` and the diff engine (2026-06-13)

This is the contract for consuming the change feed and event log from **any
language** (concepts §21). The feed is two ordinary PostgreSQL tables; this
document is everything you need to write a correct consumer without the .NET
library. Runnable references: [samples/polyglot-consumers](../../samples/polyglot-consumers/README.md).

The kernel guarantees this format is stable; treat it as the public API it is.

## 1. The change feed table: `papuma.change`

| Column | Type | Meaning |
|---|---|---|
| `seq` | `bigint` (identity, PK) | Global feed order. Your cursor. |
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
| `txid` | `xid8` | Writer's transaction id — used for gapless reads (section 4). |

## 2. The event log table: `papuma.event`

| Column | Type | Meaning |
|---|---|---|
| `seq` | `bigint` (identity, PK) | Global event order (separate from the change feed). |
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

1. **Gapless read.** Sequence numbers are assigned at INSERT but become visible
   at COMMIT, so commit order can cross seq order. Read only rows whose
   transaction is visible to everyone:

   ```sql
   SELECT seq, document_type, document_id, version, operation, diff, metadata
   FROM papuma.change
   WHERE seq > :checkpoint
     AND txid < pg_snapshot_xmin(pg_current_snapshot())
   ORDER BY seq
   LIMIT :batch;
   ```

   A naive `seq > checkpoint` loses a slow writer's lower seq permanently. This
   predicate is **mandatory**.

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

3. **Checkpoint + idempotency.** Keep your position in `papuma.checkpoint`
   (`handler_name text PK, last_seq bigint`) — pick a unique `handler_name`.
   Advance it *after* processing, in the same transaction. Delivery is
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
