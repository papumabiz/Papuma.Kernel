# Recipe: Schema for projection tables — create, evolve, rebuild

Status: pattern recipe (2026-09-26), verified against PostgreSQL 18 by
`ProjectionSchemaTests` in the kernel's test suite (the contributor below is the one
the test runs). `Papuma.Kernel.Local` has the same hook — see the SQLite section at
the end.
Background: [read models in the same database](same-database-read-models.md) (the
table and its RLS policy), [concepts §19](../concepts.md#19-checkpoints-backup-and-rebuild-what-is-truth-what-is-derivable)
(projections are derivable), [ADR-009](../adr/adr-009-projections-as-dumb-handlers.md),
[ADR-024](../adr/adr-024-projections-and-effect-handlers.md) (versioned rebuild).

The kernel creates and evolves its own schema at startup. Projection tables are the
application's — but they have one property that changes how to migrate them: **they
are derived**. Everything in them can be rebuilt from the feed. So there are only two
kinds of change, and neither needs a migration framework to start with:

| Change | How |
|---|---|
| **Additive** — new table, new nullable/defaulted column, new index, new policy | idempotent DDL, applied on every startup |
| **Breaking** — rename, type change, different key, different meaning | raise the projection's **`Version`**: the next start empties the table and replays the feed into it. For zero downtime instead: a new table filled by a handler with a new name, switch reads, drop the old table |

## 1. Where the DDL runs: a schema contributor

```csharp
builder.Services
    .AddPapumaKernel(o => { /* connection, model */ })
    .AddSchemaContributor<TicketBoardSchema>()   // after the kernel schema, before the feed workers
    .AddChangeHandler<TicketBoardProjection>();
```

Order matters twice. The policy uses `papuma.scope_visible`, which the kernel schema
creates — so the application DDL must run after it. And the projection handler writes
into the table — so the DDL must finish before the feed workers start. A contributor
runs exactly there, on every startup, when `EnsureSchema` is on (the default).

```csharp
public sealed class TicketBoardSchema : ISchemaContributor
{
    public async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            -- Several instances may start at once; concurrent CREATE ... IF NOT EXISTS can
            -- still collide. One transaction-scoped lock serializes them.
            SELECT pg_advisory_xact_lock(hashtext('app.schema'));

            CREATE SCHEMA IF NOT EXISTS app;

            -- v1
            CREATE TABLE IF NOT EXISTS app.ticket_board
            (
                scope     text   NOT NULL,
                tenant_id text   NOT NULL,
                id        text   NOT NULL,
                title     text   NOT NULL,
                version   bigint NOT NULL,
                PRIMARY KEY (scope, tenant_id, id)
            );

            -- v2: additive — a defaulted column and an index
            ALTER TABLE app.ticket_board ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'open';
            CREATE INDEX IF NOT EXISTS ix_ticket_board_status ON app.ticket_board (scope, tenant_id, status);

            -- Row-level security (ADR-019); recreated each start, so a changed policy lands too
            ALTER TABLE app.ticket_board ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.ticket_board FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS scope_isolation ON app.ticket_board;
            CREATE POLICY scope_isolation ON app.ticket_board
                USING (papuma.scope_visible(scope, tenant_id))
                WITH CHECK (papuma.scope_writable(scope, tenant_id));
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
```

The script only ever grows: v1 stays, v2 is appended, each statement idempotent. Read
the file top to bottom and you have the table's history.

**Which role runs it.** The contributor uses the kernel's data source — by default the
application's own login, which then owns `papuma.*` and `app.*` alike (`FORCE ROW LEVEL
SECURITY` keeps RLS in force for the owner too). If you separate a migration role from
the application role, grant the application role DML on the schema after creating the
tables, and set `EnsureSchema = false` where the application role runs.

## 2. Breaking changes: rebuild, don't migrate

Suppose `title` becomes `summary` plus `description`. Nothing is converted in place,
so nothing can be converted wrongly: the table is rebuilt from the truth, the same
way it would be built after a restore. Two routes:

**Raise the version (the default).** The projection declares itself (ADR-024):

```csharp
public sealed class TicketBoardProjection(NpgsqlDataSource appData) : IChangeHandler, IProjection
{
    public string Name => "ticket-board";   // stays — the identity of the checkpoint
    public int Version => 2;                // was 1: summary + description replace title

    public async Task ResetAsync(CancellationToken ct)
    {
        // Idempotent. TRUNCATE, not DELETE: it is not subject to row-level security,
        // so it empties every tenant's rows.
        await using var cmd = appData.CreateCommand("TRUNCATE app.ticket_board");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task HandleAsync(ChangeRecord change, CancellationToken ct) { /* … */ }
}
```

Change the contributor's DDL for the new shape (idempotently, as always), raise
`Version`, deploy. At the next start the processor finds the lower stored version,
calls `ResetAsync`, and replays the feed into the empty table — once, however many
instances start. Instances still running the old code pause the projection instead of
writing the old shape into it. `TRUNCATE` needs the `TRUNCATE` privilege: the table
owner has it; grant it to the application role if a separate role owns the tables.
Readers see the table incomplete until the replay catches up (`GetLagAsync`, the
dashboard).

**A new name, for zero downtime.** When readers must never see a partial table:

1. Add `app.ticket_board_v2` to the contributor (keep `ticket_board` for now).
2. Add a handler `TicketBoardProjectionV2` with `Name => "ticket-board-v2"`. A new name
   is a new checkpoint at 0 — it replays the whole feed and fills the new table while
   the old one keeps serving.
3. When its lag reaches 0, switch the reads.
4. In a later release, remove the old handler and add `DROP TABLE IF EXISTS
   app.ticket_board;` to the contributor. The old checkpoint row stays behind — delete
   it from `papuma.checkpoint` if it bothers you.

**After a kernel upgrade that asks for a rebuild**, one call rebuilds every projection
of a processor: `ResetProjectionsAsync()` — effect handlers are left alone.

## 3. Append-only projections: seq as the row key

An upsert projection is idempotent through its version guard. A projection that
*appends* a row per change — an activity stream, a timeline — has no row to guard:
an at-least-once redelivery or a replay after a reset would add every row again. Key
the rows by the change's `seq`, the identity of the source record:

```sql
CREATE TABLE IF NOT EXISTS app.activity
(
    seq         bigint      NOT NULL PRIMARY KEY,   -- the change's seq
    scope       text        NOT NULL,
    tenant_id   text        NOT NULL,
    document_id text        NOT NULL,
    version     bigint      NOT NULL,
    occurred_at timestamptz NOT NULL,
    summary     text        NOT NULL
);
```

```sql
INSERT INTO app.activity (seq, scope, tenant_id, document_id, version, occurred_at, summary)
VALUES (@seq, @scope, @tenantId, @id, @version, @occurredAt, @summary)
ON CONFLICT (seq) DO NOTHING;
```

- **Redelivery and rebuild become no-ops for rows already there** — a reset needs no
  `TRUNCATE` first, and readers never see the table empty.
- **Order the reads by `seq`, not by insertion.** Live delivery and a rebuild may
  insert concurrent transactions in a different order (ADR-022, concepts §2); `seq` is
  the write order and the same either way. `ORDER BY seq DESC` is stable across
  rebuilds; `occurred_at` works too, with `seq` as the tie-breaker.
- **Keep the newest N per tenant** by pruning after the insert —
  `DELETE … WHERE tenant_id = @tenantId AND seq < (SELECT seq … ORDER BY seq DESC
  OFFSET N - 1 LIMIT 1)`. A late, older row is inserted and pruned again; the result
  is the same as if it had arrived in order.
- The event feed works the same way, keyed by the event's `seq`.

## 4. Tests

```csharp
var store = await database.CreateStoreAsync(model);                // kernel schema + grants
await new TicketBoardSchema().EnsureSchemaAsync(database.OwnerDataSource, ct);
await database.GrantAppRoleAsync("app");                           // the app role runs RLS-checked
```

Or host the kernel in the test with `AddSchemaContributor<TicketBoardSchema>()` on the
test database's owner data source; the contributor then runs exactly as in production.

## SQLite (`Papuma.Kernel.Local`)

The same hook, with the kernel's open connection instead of a data source:

```csharp
builder.Services
    .AddPapumaKernelLocal(o => { o.DbPath = dbPath; o.Model(/* ... */); })
    .AddSchemaContributor<TicketBoardSchema>()     // an ISqliteSchemaContributor
    .AddChangeHandler<TicketBoardProjection>();

public sealed class TicketBoardSchema : ISqliteSchemaContributor
{
    public async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ticket_board
            (
                scope TEXT NOT NULL, tenant_id TEXT NOT NULL, id TEXT NOT NULL,
                title TEXT NOT NULL, version INTEGER NOT NULL,
                PRIMARY KEY (scope, tenant_id, id)
            );
            CREATE INDEX IF NOT EXISTS ix_ticket_board_title ON ticket_board (scope, tenant_id, title);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

Differences: no advisory lock (one process owns the file), no RLS (isolation is the
explicit `scope`/`tenant_id` predicate), and no `ADD COLUMN IF NOT EXISTS` — read
`pragma_table_info('ticket_board')` and add the column only when it is missing. The
projection handler writes through its own connection to the same file; the feed
processor holds no transaction while handlers run — and none while it calls
`ResetAsync` for a rebuild, so `DELETE FROM ticket_board` there cannot block on the
file's write lock. Verified by `SqliteProjectionSchemaTests` and
`SqliteProjectionLifecycleTests`.

## When to reach for a migration tool

When application tables hold data that is **not** derivable — anything a handler
cannot rebuild from the feed — they are no longer projections, and ordered,
non-idempotent migrations (DbUp, Flyway, EF Core migrations) are the right tool. Run
them from a contributor or as a deployment step; the ordering argument above still
applies.
