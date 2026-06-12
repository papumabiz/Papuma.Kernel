# ADR-010: Feed consumption — snapshot-based polling with LISTEN/NOTIFY wakeup

## Status

Accepted (2026-06-11)

## Context

The change feed is ordered by a `seq` column (identity). Naive polling with
`WHERE seq > @lastSeq` loses changes: transaction A draws `seq = 100` but commits
**after** transaction B with `seq = 101`. A poller that has already seen 101 and
advanced its checkpoint never sees 100.

The v1 analysis [polling-vs-listen-analysis.md](../../analyses/polling-vs-listen-analysis.md)
already established: LISTEN/NOTIFY alone is unsuitable as the source of truth
(connection drops, no persistence), polling alone is sluggish or expensive.

## Decision

1. **Polling is the truth, NOTIFY is only the alarm clock.** The engine polls the
   feed; a `NOTIFY papuma_changes` at the end of every save transaction wakes
   waiting pollers immediately. Missed notifications cost only latency (at most
   one poll interval), never data.
2. **Gapless reading via transaction snapshots.** Every ChangeRecord stores
   `txid = pg_current_xact_id()` (xid8). The poller reads only changes whose
   transaction is safely finished and visible to everyone:

   ```sql
   SELECT ...
   FROM papuma.change
   WHERE seq > @checkpoint
     AND txid < pg_snapshot_xmin(pg_current_snapshot())
   ORDER BY seq
   LIMIT @batchSize;
   ```

   With this, no still-open transaction with a smaller `seq` can land "behind" the
   checkpoint anymore — the checkpoint can safely be set to the highest `seq`
   read.
3. **One poll cycle per process**, distribution of changes to handlers in-process.
   Multiple competing consumer processes coordinate via `FOR UPDATE SKIP LOCKED`
   on the checkpoint table (one leader per handler group) — no distributed
   consensus in the kernel.

## Consequences

- No lost changes under normal MVCC parallelism; the approach needs no artificial
  lag windows or heuristics.
- Very long running write transactions hold back `pg_snapshot_xmin` and thus feed
  progress — accepted and observable ("feed lag" metric); long transactions are an
  anti-pattern of the write path anyway (ADR-003: one save, one short
  transaction).
- Latency in the normal case ≈ one NOTIFY roundtrip (milliseconds), in the failure
  case ≤ the poll interval.
- `xid8` is wraparound-safe; no special handling needed.
