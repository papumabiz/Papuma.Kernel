# Polyglot feed consumers (Python & Go)

The change feed is **not a .NET-private artifact** — it is two ordinary
PostgreSQL tables with a documented, stable wire format (concepts §21, ADR-022;
full contract in [feed-wire-format.md](../../docs/feed-wire-format.md)).
Any language can consume it. These two clients prove the "~150 lines" claim: a
[Python](python/consumer.py) and a [Go](go/main.go) consumer, each implementing
the full pattern.

## What both clients do (and what every consumer must)

1. **Set the scope (RLS).** A cross-tenant projection sees every scope via
   `set_config('app.current_scope', 'All', true)`. It is transaction-local, so it
   runs inside the read transaction (concepts §23). A missing scope means *empty
   reads*, never a leak (fail-closed).
2. **Read by snapshot cursor.** The position is a transaction snapshot, not a
   `seq`: each cycle takes `pg_current_snapshot()` as a *slice* and reads the rows
   of every transaction visible in it but not in the last completed one, in `seq`
   order (concepts §2, [feed-wire-format.md §4](../../docs/feed-wire-format.md)).
   Any `seq > checkpoint` poll — including the 1.0–1.4 `txid < xmin` variant —
   loses rows of interleaved transactions. **Mandatory, not an optimization.**
3. **Persist the cursor after success, be idempotent.** Each consumer keeps its
   own row in `papuma.checkpoint` (just pick a unique `handler_name`). At-least-once
   delivery means a crash before commit re-delivers the batch — processing must
   be idempotent. Order is commit order (per document by version): a lower `seq`
   can arrive after a higher one.
4. **Wake on NOTIFY, poll for truth.** `LISTEN papuma_changes` gives millisecond
   latency; a missed signal costs at most one poll interval.

The diff is the **policy-applied** reversible field diff (ADR-004/007): the
clients print the changed field *paths*, and values of `[SensitiveData]` fields
(e.g. an order's `customerEmail`) are already redacted in the feed — a
foreign-language reader structurally cannot reach them. The feed is safe reading
material in every language.

## Run them

Point them at a running Papuma database (e.g. the sample app's). Use
**`127.0.0.1`, not `localhost`** — container ports are published on IPv4, and a
`localhost` that resolves to `::1` first stalls in a connection timeout.

```bash
# Go (pgx) — go run pulls the driver into the module cache
cd go
PAPUMA_CONN="postgres://postgres:postgres@127.0.0.1:5432/papuma_sample" go run .

# Python (psycopg 3)
cd python
python -m venv .venv && .venv/bin/pip install -r requirements.txt   # Windows: .venv\Scripts\pip
PAPUMA_CONN="postgresql://postgres:postgres@127.0.0.1:5432/papuma_sample" .venv/bin/python consumer.py
```

Example output (after the sample app created a product, an order and a
replenish — verified 2026-09-27 on PostgreSQL 18):

```
seq=1    Insert Product/5979d8a6… v1 changed=[id name price]
seq=2    Insert Inventory/5979d8a6… v1 changed=[id stock]
seq=3    Update Inventory/5979d8a6… v2 changed=[stock]
seq=4    Insert Order/a493e01f… v1 changed=[id total status quantity productId customerEmail]
seq=5    Update Inventory/5979d8a6… v3 changed=[stock]
```

Interleaved transactions arrive in commit order. A writes (`seq 6`), B writes
(`seq 7`), A writes again (`seq 8`) and commits; B commits 12 s later — both
clients print, verified live:

```
seq=6    Insert Interleave/a1 v1 changed=[k]
seq=8    Insert Interleave/a2 v1 changed=[k]
seq=7    Insert Interleave/b1 v1 changed=[k]
```

The predicate of 1.0–1.4 delivered 6 and 8, moved the checkpoint to 8 — and never
delivered 7.

## The event log is identical

To consume the event log instead, read `papuma.event` (columns `seq, scope,
tenant_id, event_type, payload, metadata, occurred_at, txid`) with the same
snapshot cursor and a separate checkpoint name. There is no `operation`/`diff`
— the `payload` is the policy-applied fact (ADR-013).

## When to reach for a bus instead

These direct-SQL clients are path A of concepts §21: simple, same-database
consumers. They do not get the engine's retry/backoff, poison handling or leader
coordination — a critical consumer rebuilds those, or uses path B (a .NET bridge
handler publishing to NATS/Kafka, see the [nats-bridge recipe](../../docs/recipes/nats-bridge.md)).
