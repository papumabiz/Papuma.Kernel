"""A polyglot change-feed consumer in Python (concepts §21, ADR-010).

The feed is two ordinary Postgres tables with a documented wire format — any
language can consume it. This client is the whole pattern in ~70 lines: gapless
reads, at-least-once with a persisted checkpoint, RLS scope.

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


def drain(conn: psycopg.Connection) -> int:
    """Process one batch in a single transaction and return how many were handled."""
    with conn.transaction():
        cur = conn.cursor()

        # RLS (§23): a cross-tenant projection sees every scope. set_config(..., true)
        # is transaction-local, so it must run inside this transaction.
        cur.execute("SELECT set_config('app.current_scope', 'All', true)")

        # Read our checkpoint, creating it at 0 on the first run.
        cur.execute(
            """
            INSERT INTO papuma.checkpoint (handler_name, last_seq) VALUES (%s, 0)
            ON CONFLICT (handler_name) DO UPDATE SET handler_name = papuma.checkpoint.handler_name
            RETURNING last_seq
            """,
            (HANDLER,),
        )
        checkpoint = cur.fetchone()[0]

        # The gapless predicate (§2): only changes whose transaction is visible to
        # everyone. Without it a slow writer's lower seq could land behind the
        # checkpoint and be lost silently.
        cur.execute(
            """
            SELECT seq, document_type, document_id, version, operation, diff
            FROM papuma.change
            WHERE seq > %s AND txid < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY seq
            LIMIT %s
            """,
            (checkpoint, BATCH),
        )
        rows = cur.fetchall()

        for seq, doc_type, doc_id, version, operation, diff in rows:
            # Process the change. Handlers MUST be idempotent (at-least-once): a crash
            # before commit re-delivers the batch. psycopg returns jsonb as a dict;
            # the diff is the policy-applied reversible field diff (ADR-004/007), so
            # values of sensitive fields never appear.
            changed = list(diff.keys())
            print(f"seq={seq:<4} {_OPS.get(operation, '?'):<6} "
                  f"{doc_type}/{doc_id} v{version} changed={changed}")

        if rows:
            cur.execute(
                "UPDATE papuma.checkpoint SET last_seq = %s, updated_at = now() "
                "WHERE handler_name = %s",
                (rows[-1][0], HANDLER),
            )

        return len(rows)


def main() -> None:
    with psycopg.connect(CONN, autocommit=True) as conn:
        # NOTIFY is the alarm clock, polling is the truth (ADR-010).
        conn.execute("LISTEN papuma_changes")
        print(f'consuming the change feed as "{HANDLER}" — Ctrl+C to stop')
        while True:
            if drain(conn) == 0:
                # Wait for a NOTIFY wakeup or a 5s timeout, then drain again.
                select.select([conn.fileno()], [], [], 5.0)


if __name__ == "__main__":
    main()
