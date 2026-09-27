# ADR-022 — Snapshot cursor: the feed follows commit order

## Status

Accepted (2026-09-27) — supersedes point 2 of [ADR-010](adr-010-feed-consumption.md)
(gapless reading) and amends the ordering statement of
[ADR-009](adr-009-projections-as-dumb-handlers.md). PostgreSQL kernel only.

## Context

ADR-010 made the feed "gapless" with one predicate:

```sql
WHERE seq > @checkpoint AND txid < pg_snapshot_xmin(pg_current_snapshot())
```

It assumes that a transaction with an older id also draws its sequence numbers
earlier. That holds for single-write transactions and fails for the normal case of
a session writing several times (jejak feedback F-15, reproduced deterministically in
`FeedGapTests`):

| step | transaction | seq |
|---|---|---|
| A writes | A (older id) | 96 |
| B writes | B (newer id) | 97 |
| A writes again, commits | A | 98 |

While B is open, `xmin = B`: A's rows 96 and 98 pass `txid < xmin`, the checkpoint
moves to 98, and when B commits its row 97 lies behind the checkpoint — never
delivered, by any handler, without a failure entry. The records themselves are intact
in `papuma.change`; only delivery skips them.

No predicate over a single sequence number can fix this: to move a `seq` cursor safely,
the reader would have to know the sequence numbers of transactions that are still in
progress, and those rows are invisible to it. What *is* observable is which
transactions have committed — the transaction snapshot.

## Decision

### 1. The cursor is a transaction snapshot, not a sequence number

This is the batching model of PgQ (Skype's queue on PostgreSQL). A handler's position
is the set of transactions it has seen, stored as a `pg_snapshot`. Each cycle takes
the current snapshot and delivers the rows of every transaction that is visible in it
but not in the stored one — a *slice*. A transaction's rows therefore become
deliverable together, when it commits, and nothing can ever appear behind the cursor.

### 2. Checkpoint state

`papuma.checkpoint` gains four columns; together with `last_seq` a handler's state is:

| column | meaning |
|---|---|
| `done_snapshot pg_snapshot` | every transaction visible in it is fully delivered; `NULL` = not yet, see `base_seq` |
| `base_seq bigint` | while `done_snapshot` is `NULL`: the rows with `seq ≤ base_seq` count as delivered (`0` for a new or reset handler; the old `last_seq` for a migrated one) |
| `slice_snapshot pg_snapshot` | the slice in progress: its target snapshot; `NULL` = none |
| `slice_seq bigint` | within the slice: rows with `seq ≤ slice_seq` are delivered |
| `last_seq bigint` | highest `seq` delivered so far — informational (dashboard, lag display) |

A row is **delivered** when
`done_snapshot IS NULL ? seq ≤ base_seq : pg_visible_in_snapshot(txid, done_snapshot)`,
or when it lies in the current slice at or below `slice_seq`. New handler, reset and
migration are the same state with a different `base_seq` — no special cases.

### 3. One cycle

Inside the processor's transaction, after the leader lock
(`FOR UPDATE SKIP LOCKED` on the checkpoint row, unchanged from ADR-010):

1. If `slice_snapshot` is `NULL`, take `pg_current_snapshot()` as the new slice.
   (READ COMMITTED: every later statement sees at least what this snapshot shows as
   committed.) With a `done_snapshot`, find the slice's lowest `seq` once through the
   `txid` index and set `slice_seq` just below it; no row means nothing has committed
   since — the cycle ends without taking the slice.
2. Read the next batch of the slice — a forward PK range scan:

   ```sql
   SELECT … FROM papuma.change
   WHERE seq > @slice_seq                      -- (greatest(@base_seq, @slice_seq) while done is NULL)
     AND pg_visible_in_snapshot(txid, @slice)
     AND NOT pg_visible_in_snapshot(txid, @done)  -- (omitted while done is NULL)
   ORDER BY seq
   LIMIT @batchSize;
   ```

   Reading batches through the `txid` index instead would re-read and re-sort the
   whole slice for every batch — quadratic in the slice size, which after an outage or
   a large import is unbounded. Paging the PK reads each row once; the one-off cost is
   a slice that contains an old transaction: its first batches walk forward over the
   rows committed since that transaction's first write, once.

3. Deliver in `seq` order with the existing retry, backoff, poison and stop-the-line
   rules; `slice_seq` follows the last processed row.
4. If the batch was not full and nothing stopped the line, the slice is complete:
   `done_snapshot = slice_snapshot`, `slice_snapshot = NULL`, `slice_seq = 0`.
5. `last_seq = max(last_seq, highest delivered seq)`; commit.

An empty first read leaves the checkpoint untouched (no write per idle cycle). The set
of rows in a slice is fixed when it is taken — all of its transactions have committed —
so paginating it by `seq` neither skips nor repeats.

### 4. The ordering guarantee

Delivery follows **commit order**: a transaction's changes arrive after those of every
transaction that committed before it started. Within one transaction, and therefore
within one slice, by `seq`. **Per document strictly by version**: two transactions
writing the same document serialize on its row lock, so the later one commits later and
drew later sequence numbers. What no longer holds is "globally by `seq`": a handler may
receive seq 97 after seq 98. That promise was never kept (F-15) — commit order is the
order the database itself defines, and unlike `seq` order it respects causality.

`seq` stays the record's identity: failure entries, the idempotency key
`handler + seq`, `GetHistoryAsync` and `GetChangesByCorrelationAsync` are unchanged.

### 5. Lag is counted, not subtracted

`head − checkpoint` has no meaning any more. Lag becomes the number of **committed,
undelivered** rows, counted. It is exact, and it is what the health check's threshold
always meant.

"Not visible in `done`" is evaluated as two disjoint `txid` index conditions — `txid >=
pg_snapshot_xmax(done)` plus `txid = ANY(pg_snapshot_xip(done))` — both for the lag and
for the slice's lowest `seq`. The equivalent `txid >= pg_snapshot_xmin(done) AND NOT
visible` would scan every row since the oldest transaction open when `done` was taken;
an idle-in-transaction session would turn each health check into a table scan. Checked
with `EXPLAIN ANALYZE` on 2 million rows, custom and generic plans alike.

### 6. Indexes

`papuma.change` and `papuma.event` get an index on `txid` (created idempotently by
`EnsureSchemaAsync`; on large tables, create it `CONCURRENTLY` before upgrading).

### 7. Migration and logical restore

`EnsureSchemaAsync` adds the columns and sets `base_seq = last_seq` once for existing
checkpoints: the first slice after the upgrade delivers every committed row above the
old checkpoint, nothing below it. Rows lost before the upgrade stay lost — rebuild the
projections (`ResetCheckpointAsync`) after upgrading.

Transaction ids belong to a cluster. A logical restore (`pg_dump`/`pg_restore`) into
another cluster keeps the rows' `txid` values and the stored snapshots, but restarts
the id counter — stored snapshots would then declare every new transaction "already
delivered", and restored `txid`s would never become visible. `EnsureSchemaAsync`
detects that state (a stored `txid` at or beyond the next id to be assigned, or a
snapshot `xmax` beyond it — impossible within one cluster) and repairs it in one
transaction: each checkpoint falls back to a `seq` floor just below its lowest
undelivered row (old ids and old snapshots are consistent with each other, so
"undelivered" is still exact; delivered rows above the floor come again, at-least-once),
with no snapshot; then the restored `txid`s are set to the frozen id (visible to every
snapshot). The same repair is callable explicitly
(`SchemaManager.RepairFeedAfterLogicalRestoreAsync`). Physical backups, PITR and
`pg_upgrade` keep the id space and need nothing.

### 8. Scope

The event feed uses the same cursor with its own checkpoint rows (`event:` prefix).
`Papuma.Kernel.Local` is unaffected: SQLite's single writer serializes whole
transactions, so its `seq` order already is commit order. The wire-format document and
the polyglot samples adopt the slice query; consumers that implemented the ADR-010
predicate have the same defect and must switch.

## Consequences

- **Positive:** No committed change can be skipped; the defect class is gone by
  construction, not by a tighter heuristic.
- **Positive:** A long-running or idle-in-transaction session no longer stalls the whole
  feed — only its own rows wait. Under ADR-010 any open transaction pinned `xmin`.
- **Positive:** The ordering guarantee matches the database's commit order and causality.
- **Negative:** "Strictly by `seq`" is gone. Handlers that treat `seq` as a monotonic
  watermark (`skip if seq ≤ last seen`) would drop late rows; the documented
  idempotency is per `handler + seq` or per document version, which stays correct.
- **Negative:** One more index per change and event insert (`txid`).
- **Negative:** Direct-SQL consumers need snapshot arithmetic instead of a number — more
  reason for the bridge path (concepts §21).
- **Neutral:** Proven by a concurrency stress test (interleaved multi-write sessions,
  random commit and rollback order, processor running throughout): every committed row
  delivered, none rolled back, per-document order intact.

## Alternatives considered

- **Tighter `seq` predicate.** Needs the sequence numbers of in-progress transactions,
  which are invisible. Workarounds (advisory locks keyed by `seq`, in-flight registries)
  leak kernel state into lock tables or fight MVCC.
- **Assign `seq` at commit, under a global lock.** Keeps "strictly by `seq`" and the
  numeric checkpoint, but serializes the commit phase of every writing transaction in
  the database (including the commit flush) and adds a write per change row — a hard
  throughput ceiling to preserve a wording.
- **Logical decoding (a replication slot, as Debezium does).** Exact commit order, but
  an operational dependency (slot management, WAL retention, `wal_level = logical`)
  that the kernel's "just PostgreSQL" stance rejects.
