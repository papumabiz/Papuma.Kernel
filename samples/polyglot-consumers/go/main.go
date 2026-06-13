// A polyglot change-feed consumer in Go (concepts §21, ADR-010).
//
// The feed is two ordinary Postgres tables with a documented wire format — any
// language can consume it. This client is the whole pattern in ~80 lines:
// gapless reads, at-least-once with a persisted checkpoint, RLS scope.
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
		n, err := drain(ctx, conn)
		if err != nil {
			log.Printf("drain failed (retrying): %v", err)
			time.Sleep(2 * time.Second)
			continue
		}
		if n == 0 {
			waitCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
			_, _ = conn.WaitForNotification(waitCtx) // returns early on NOTIFY, else times out
			cancel()
		}
	}
}

// drain processes one batch in a single transaction: set scope, read the
// checkpoint, read the gapless batch, process it, advance the checkpoint, commit.
func drain(ctx context.Context, conn *pgx.Conn) (int, error) {
	tx, err := conn.Begin(ctx)
	if err != nil {
		return 0, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // no-op after a successful commit

	// RLS (§23): a cross-tenant projection sees every scope. set_config(..., true)
	// is transaction-local, so it must run inside this transaction.
	if _, err := tx.Exec(ctx, "SELECT set_config('app.current_scope', 'All', true)"); err != nil {
		return 0, err
	}

	// Read our checkpoint, creating it at 0 on the first run.
	var checkpoint int64
	err = tx.QueryRow(ctx, `
		INSERT INTO papuma.checkpoint (handler_name, last_seq) VALUES ($1, 0)
		ON CONFLICT (handler_name) DO UPDATE SET handler_name = papuma.checkpoint.handler_name
		RETURNING last_seq`, handlerName).Scan(&checkpoint)
	if err != nil {
		return 0, err
	}

	// The gapless predicate (§2): only changes whose transaction is visible to
	// everyone. Without it, a slow writer's lower seq could land behind the
	// checkpoint and be lost silently.
	rows, err := tx.Query(ctx, `
		SELECT seq, document_type, document_id, version, operation, diff
		FROM papuma.change
		WHERE seq > $1 AND txid < pg_snapshot_xmin(pg_current_snapshot())
		ORDER BY seq
		LIMIT $2`, checkpoint, batchSize)
	if err != nil {
		return 0, err
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
			return 0, err
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
		return 0, err
	}

	if count > 0 {
		if _, err := tx.Exec(ctx,
			`UPDATE papuma.checkpoint SET last_seq = $2, updated_at = now() WHERE handler_name = $1`,
			handlerName, last); err != nil {
			return 0, err
		}
	}

	return count, tx.Commit(ctx)
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
