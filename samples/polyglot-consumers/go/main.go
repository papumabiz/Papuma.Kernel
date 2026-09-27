// A polyglot change-feed consumer in Go (concepts §21, ADR-022).
//
// The feed is two ordinary Postgres tables with a documented wire format — any
// language can consume it. This client is the whole pattern in ~200 lines: the
// snapshot cursor, at-least-once with a persisted position, RLS scope.
//
//	PAPUMA_CONN=postgres://postgres:postgres@localhost:5432/papuma_sample go run .
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"time"

	"github.com/jackc/pgx/v5"
)

const (
	handlerName = "go-consumer" // our own row in papuma.checkpoint
	batchSize   = 100
)

const columns = "seq, document_type, document_id, version, operation, diff"

// A new slice and its lowest seq, found once through the txid index (feed-wire-format.md
// §4): "not visible in done" = at or beyond its xmax, or in progress when it was taken.
const newSlice = `
	WITH s AS MATERIALIZED (SELECT pg_current_snapshot() AS slice),
	     r AS MATERIALIZED (
	         SELECT c.seq FROM papuma.change c, s
	         WHERE c.txid >= pg_snapshot_xmax($1::text::pg_snapshot)
	           AND pg_visible_in_snapshot(c.txid, s.slice)
	         UNION ALL
	         SELECT c.seq FROM papuma.change c, s
	         WHERE c.txid = ANY (ARRAY(SELECT pg_snapshot_xip($1::text::pg_snapshot)))
	           AND pg_visible_in_snapshot(c.txid, s.slice))
	SELECT (SELECT slice::text FROM s), (SELECT min(seq) FROM r)`

// The next rows of the slice: transactions visible in the slice snapshot but not in the
// done one, in seq order — a forward PK range scan. Before the first completed slice,
// "done" is a seq floor instead.
const (
	firstSlice = `SELECT ` + columns + ` FROM papuma.change
		WHERE seq > $1
		  AND pg_visible_in_snapshot(txid, $2::text::pg_snapshot)
		ORDER BY seq LIMIT $3`
	nextSlice = `SELECT ` + columns + ` FROM papuma.change
		WHERE seq > $1
		  AND pg_visible_in_snapshot(txid, $2::text::pg_snapshot)
		  AND NOT pg_visible_in_snapshot(txid, $4::text::pg_snapshot)
		ORDER BY seq LIMIT $3`
)

func main() {
	ctx := context.Background()
	connString := os.Getenv("PAPUMA_CONN")
	if connString == "" {
		connString = "postgres://postgres:postgres@localhost:5432/papuma_sample"
	}

	conn, err := pgx.Connect(ctx, connString)
	if err != nil {
		log.Fatalf("connect: %v", err)
	}
	defer conn.Close(ctx)

	// NOTIFY is the alarm clock, polling is the truth (ADR-010): LISTEN lets us
	// react in milliseconds, but a missed signal only costs one poll interval.
	if _, err := conn.Exec(ctx, "LISTEN papuma_changes"); err != nil {
		log.Fatalf("listen: %v", err)
	}
	log.Printf("consuming the change feed as %q — Ctrl+C to stop", handlerName)

	for {
		more, err := drain(ctx, conn)
		if err != nil {
			log.Printf("drain failed (retrying): %v", err)
			time.Sleep(2 * time.Second)
			continue
		}
		if !more {
			waitCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
			_, _ = conn.WaitForNotification(waitCtx) // returns early on NOTIFY, else times out
			cancel()
		}
	}
}

// drain processes one batch in a single transaction: set scope, lock the cursor,
// read the next rows of its slice, process them, persist the cursor, commit. It
// reports whether another cycle may find more.
func drain(ctx context.Context, conn *pgx.Conn) (bool, error) {
	tx, err := conn.Begin(ctx)
	if err != nil {
		return false, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // no-op after a successful commit

	// RLS (§23): a cross-tenant projection sees every scope. set_config(..., true)
	// is transaction-local, so it must run inside this transaction.
	if _, err := tx.Exec(ctx, "SELECT set_config('app.current_scope', 'All', true)"); err != nil {
		return false, err
	}

	// Our cursor, locked: one consumer per handler name. A new row replays the
	// whole feed (base_seq 0).
	if _, err := tx.Exec(ctx,
		"INSERT INTO papuma.checkpoint (handler_name) VALUES ($1) ON CONFLICT DO NOTHING",
		handlerName); err != nil {
		return false, err
	}
	var baseSeq, sliceSeq int64
	var done, slice *string
	err = tx.QueryRow(ctx, `
		SELECT base_seq, done_snapshot::text, slice_snapshot::text, slice_seq
		FROM papuma.checkpoint WHERE handler_name = $1 FOR UPDATE`, handlerName).
		Scan(&baseSeq, &done, &slice, &sliceSeq)
	if err != nil {
		return false, err
	}
	resumed := slice != nil

	// No slice in progress: the transactions committed by now form the next one.
	// A seq checkpoint cannot work here — it would need the seqs of transactions
	// that are still open (concepts §2).
	if slice == nil && done == nil {
		var current string
		if err := tx.QueryRow(ctx, "SELECT pg_current_snapshot()::text").Scan(&current); err != nil {
			return false, err
		}
		slice, sliceSeq = &current, 0
	} else if slice == nil {
		var current string
		var lowest *int64
		if err := tx.QueryRow(ctx, newSlice, *done).Scan(&current, &lowest); err != nil {
			return false, err
		}
		if lowest == nil {
			return false, tx.Commit(ctx) // nothing committed since the done snapshot
		}
		slice, sliceSeq = &current, *lowest-1
	}

	query, floor := nextSlice, sliceSeq
	args := []any{floor, *slice, batchSize, done}
	if done == nil {
		query, args = firstSlice, []any{max(baseSeq, sliceSeq), *slice, batchSize}
	}
	rows, err := tx.Query(ctx, query, args...)
	if err != nil {
		return false, err
	}

	var last int64
	count := 0
	for rows.Next() {
		var seq, version int64
		var docType, docID string
		var operation int16
		var diff []byte
		if err := rows.Scan(&seq, &docType, &docID, &version, &operation, &diff); err != nil {
			rows.Close()
			return false, err
		}

		// Process the change. Handlers MUST be idempotent (at-least-once): a crash
		// before commit re-delivers the batch. Here we just print the changed paths;
		// the diff is the policy-applied reversible field diff (ADR-004/007) — values
		// of sensitive fields never appear.
		var fields map[string]json.RawMessage
		_ = json.Unmarshal(diff, &fields)
		changed := make([]string, 0, len(fields))
		for k := range fields {
			changed = append(changed, k)
		}
		fmt.Printf("seq=%-4d %-6s %s/%s v%d changed=%v\n", seq, opName(operation), docType, docID, version, changed)

		last = seq
		count++
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return false, err
	}

	// A short batch completes the slice; a full one moves the position inside it.
	if count < batchSize {
		done, slice, sliceSeq = slice, nil, 0
	} else {
		sliceSeq = last
	}

	if count > 0 || resumed { // an empty fresh slice changes nothing — no write
		if _, err := tx.Exec(ctx, `
			UPDATE papuma.checkpoint
			SET done_snapshot = $2::text::pg_snapshot, slice_snapshot = $3::text::pg_snapshot,
			    slice_seq = $4, last_seq = greatest(last_seq, $5), updated_at = now()
			WHERE handler_name = $1`,
			handlerName, done, slice, sliceSeq, last); err != nil {
			return false, err
		}
	}

	return count > 0 || resumed, tx.Commit(ctx)
}

func opName(operation int16) string {
	switch operation {
	case 1:
		return "Insert"
	case 2:
		return "Update"
	case 3:
		return "Delete"
	default:
		return "?"
	}
}
