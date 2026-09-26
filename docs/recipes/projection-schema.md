# Recipe: Schema for projection tables — create, evolve, rebuild

Status: pattern recipe (2026-09-26), verified against PostgreSQL 18 by
`ProjectionSchemaTests` in the kernel's test suite (the contributor below is the one
the test runs). PostgreSQL kernel only.
Background: [read models in the same database](same-database-read-models.md) (the
table and its RLS policy), [concepts §19](../concepts.md#19-checkpoints-backup-and-rebuild-what-is-truth-what-is-derivable)
(projections are derivable), [ADR-009](../adr/adr-009-projections-as-dumb-handlers.md).

The kernel creates and evolves its own schema at startup. Projection tables are the
application's — but they have one property that changes how to migrate them: **they
are derived**. Everything in them can be rebuilt from the feed. So there are only two
kinds of change, and neither needs a migration framework to start with:

| Change | How |
|---|---|
| **Additive** — new table, new nullable/defaulted column, new index, new policy | idempotent DDL, applied on every startup |
| **Breaking** — rename, type change, different key, different meaning | a new table filled by a handler with a **new name** (replays the feed from the start), then switch reads, then drop the old table |

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

Suppose `title` becomes `summary` plus `description`. Instead of an `ALTER` with a data
conversion:

1. Add `app.ticket_board_v2` to the contributor (keep `ticket_board` for now).
2. Add a handler `TicketBoardProjectionV2` with `Name => "ticket-board-v2"`. A new name
   is a new checkpoint at 0 — it replays the whole feed and fills the new table while
   the old one keeps serving.
3. When its lag reaches 0 (`GetLagAsync`, the dashboard), switch the reads.
4. In a later release, remove the old handler and add `DROP TABLE IF EXISTS
   app.ticket_board;` to the contributor.

Nothing is converted in place, so nothing can be converted wrongly: the new table is
built from the truth, the same way it would be built after a restore. Resetting the old
handler's checkpoint and truncating its table does the same in one step, but leaves
readers with an empty table until the replay catches up.

## 3. Tests

```csharp
var store = await database.CreateStoreAsync(model);                // kernel schema + grants
await new TicketBoardSchema().EnsureSchemaAsync(database.OwnerDataSource, ct);
await database.GrantAppRoleAsync("app");                           // the app role runs RLS-checked
```

Or host the kernel in the test with `AddSchemaContributor<TicketBoardSchema>()` on the
test database's owner data source; the contributor then runs exactly as in production.

## When to reach for a migration tool

When application tables hold data that is **not** derivable — anything a handler
cannot rebuild from the feed — they are no longer projections, and ordered,
non-idempotent migrations (DbUp, Flyway, EF Core migrations) are the right tool. Run
them from a contributor or as a deployment step; the ordering argument above still
applies.
