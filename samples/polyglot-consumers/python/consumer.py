"""A polyglot change-feed consumer in Python (concepts §21, ADR-022).

The feed is two ordinary Postgres tables with a documented wire format — any
language can consume it. This client is the whole pattern in ~100 lines: the
snapshot cursor, at-least-once with a persisted position, RLS scope.

    PAPUMA_CONN=postgresql://postgres:postgres@localhost:5432/papuma_sample \\
        python consumer.py

Requires psycopg 3 (`pip install -r requirements.txt`).
"""

import os
import select

import psycopg

HANDLER = "python-consumer"  # our own row in papuma.checkpoint
BATCH = 100
CONN = os.environ.get(
    "PAPUMA_CONN", "postgresql://postgres:postgres@localhost:5432/papuma_sample"
)

_OPS = {1: "Insert", 2: "Update", 3: "Delete"}

_COLUMNS = "seq, document_type, document_id, version, operation, diff"

# The next rows of the slice (feed-wire-format.md §4): transactions visible in the
# slice snapshot but not in the done one, in seq order. Before the first completed
# slice, "done" is a seq floor instead.
_FIRST_SLICE = f"""
    SELECT {_COLUMNS} FROM papuma.change
    WHERE seq > %(floor)s
      AND pg_visible_in_snapshot(txid, %(slice)s::text::pg_snapshot)
    ORDER BY seq LIMIT %(batch)s
"""
_NEXT_SLICE = f"""
    SELECT {_COLUMNS} FROM papuma.change
    WHERE txid >= pg_snapshot_xmin(%(done)s::text::pg_snapshot)
      AND txid < pg_snapshot_xmax(%(slice)s::text::pg_snapshot)
      AND pg_visible_in_snapshot(txid, %(slice)s::text::pg_snapshot)
      AND NOT pg_visible_in_snapshot(txid, %(done)s::text::pg_snapshot)
      AND seq > %(floor)s
    ORDER BY seq LIMIT %(batch)s
"""


def drain(conn: psycopg.Connection) -> bool:
    """Process one batch in a single transaction; True if another cycle may find more."""
    with conn.transaction():
        cur = conn.cursor()

        # RLS (§23): a cross-tenant projection sees every scope. set_config(..., true)
        # is transaction-local, so it must run inside this transaction.
        cur.execute("SELECT set_config('app.current_scope', 'All', true)")

        # Our cursor, locked: one consumer per handler name. A new row replays the
        # whole feed (base_seq 0).
        cur.execute(
            "INSERT INTO papuma.checkpoint (handler_name) VALUES (%s) ON CONFLICT DO NOTHING",
            (HANDLER,),
        )
        cur.execute(
            """
            SELECT base_seq, done_snapshot::text, slice_snapshot::text, slice_seq
            FROM papuma.checkpoint WHERE handler_name = %s FOR UPDATE
            """,
            (HANDLER,),
        )
        base_seq, done, slice_, slice_seq = cur.fetchone()
        resumed = slice_ is not None

        # No slice in progress: the transactions committed by now form the next one.
        # A seq checkpoint cannot work here — it would need the seqs of transactions
        # that are still open (concepts §2).
        if slice_ is None:
            slice_ = cur.execute("SELECT pg_current_snapshot()::text").fetchone()[0]
            slice_seq = 0

        params = {"done": done, "slice": slice_, "batch": BATCH,
                  "floor": max(base_seq, slice_seq) if done is None else slice_seq}
        rows = cur.execute(_FIRST_SLICE if done is None else _NEXT_SLICE, params).fetchall()

        for seq, doc_type, doc_id, version, operation, diff in rows:
            # Process the change. Handlers MUST be idempotent (at-least-once): a crash
            # before commit re-delivers the batch. psycopg returns jsonb as a dict;
            # the diff is the policy-applied reversible field diff (ADR-004/007), so
            # values of sensitive fields never appear.
            changed = list(diff.keys())
            print(f"seq={seq:<4} {_OPS.get(operation, '?'):<6} "
                  f"{doc_type}/{doc_id} v{version} changed={changed}")

        # A short batch completes the slice; a full one moves the position inside it.
        if len(rows) < BATCH:
            done, slice_, slice_seq = slice_, None, 0
        else:
            slice_seq = rows[-1][0]

        if rows or resumed:  # an empty fresh slice changes nothing — no write
            cur.execute(
                """
                UPDATE papuma.checkpoint
                SET done_snapshot = %s::text::pg_snapshot, slice_snapshot = %s::text::pg_snapshot,
                    slice_seq = %s, last_seq = greatest(last_seq, %s), updated_at = now()
                WHERE handler_name = %s
                """,
                (done, slice_, slice_seq, max((r[0] for r in rows), default=0), HANDLER),
            )

        return bool(rows) or resumed


def main() -> None:
    with psycopg.connect(CONN, autocommit=True) as conn:
        # NOTIFY is the alarm clock, polling is the truth (ADR-010).
        conn.execute("LISTEN papuma_changes")
        print(f'consuming the change feed as "{HANDLER}" — Ctrl+C to stop')
        while True:
            if not drain(conn):
                # Wait for a NOTIFY wakeup or a 5s timeout, then drain again.
                select.select([conn.fileno()], [], [], 5.0)


if __name__ == "__main__":
    main()
