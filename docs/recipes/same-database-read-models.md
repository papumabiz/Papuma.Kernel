# Recipe: Read models in the same database — with row-level security

Status: pattern recipe (2026-09-25), verified against PostgreSQL 18 by
`ScopeFunctionTests` in the kernel's test suite (the handler below is the one
the test runs). PostgreSQL kernel only — `Papuma.Kernel.Local` has no RLS.
Background: [ADR-019](../adr/adr-019-scope-predicates-for-application-tables.md)
(the contract), [concepts §23](../concepts.md#23-why-tenant-isolation-fails-closed-and-why-two-layers-not-one)
(why fail-closed), [external read models](external-read-models.md) (the same
handler rules for systems outside PostgreSQL).

The most common projection target is a table next to the kernel's own. Without
this recipe it has no row-level security: one query that forgets
`WHERE tenant_id = …` returns every tenant's rows. With it, your table isolates
exactly like `papuma.document` — a missing scope yields empty reads.

## 1. The table and its policy

```sql
CREATE TABLE app.ticket_list
(
    scope     text   NOT NULL,   -- 'Tenant' or 'Platform', from ChangeRecord.Scope
    tenant_id text   NOT NULL,   -- empty string for platform rows
    id        text   NOT NULL,
    title     text   NOT NULL,
    version   bigint NOT NULL,   -- document version, guards against stale replays
    PRIMARY KEY (scope, tenant_id, id)
);

ALTER TABLE app.ticket_list ENABLE ROW LEVEL SECURITY;
ALTER TABLE app.ticket_list FORCE ROW LEVEL SECURITY;   -- the owner too

CREATE POLICY scope_isolation ON app.ticket_list
    USING      (papuma.scope_visible(scope, tenant_id))
    WITH CHECK (papuma.scope_writable(scope, tenant_id));

GRANT SELECT, INSERT, UPDATE, DELETE ON app.ticket_list TO app_role;
GRANT USAGE ON SCHEMA papuma TO app_role;               -- to call the functions
```

`papuma.scope_visible` / `papuma.scope_writable` are created by
`EnsureSchemaAsync` and decide exactly like the kernel's own policies:

| Scope set in the transaction | sees | may write |
|---|---|---|
| `SetScopeAsync(tx, Tenant(x))` | rows of tenant `x` | rows of tenant `x` |
| `SetScopeAsync(tx, Platform())` | platform rows | platform rows |
| `SetAllScopesAsync(tx)` | every row | nothing |
| none | nothing | nothing |

Write your policy against the functions, never against the settings they read
— the functions are the contract (ADR-019), the setting names are not.

## 2. The projection handler

```csharp
public sealed class TicketListProjection(DocumentStore store, NpgsqlDataSource appData)
    : IChangeHandler
{
    public string Name => "ticket-list";   // checkpoint identity — never rename

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != "Ticket") return;
        if (change.Operation != ChangeOperation.Delete && !change.FieldChanged("title")) return;

        // The diff is the trigger, the state the payload — loaded in the change's scope.
        DocumentResult<Ticket>? current = null;
        if (change.Operation != ChangeOperation.Delete)
        {
            await using var session = store.OpenSession(change.Scope);
            current = await session.LoadAsync<Ticket>(change.DocumentId, ct);
        }

        await using var conn = await appData.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(tx, change.Scope, ct);   // this change's tenant, not All

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("scope", change.Scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", change.Scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", change.DocumentId);

        if (current is null)   // deleted — now, or by a later change not yet delivered
        {
            cmd.CommandText = """
                DELETE FROM app.ticket_list
                WHERE scope = @scope AND tenant_id = @tenantId AND id = @id
                """;
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO app.ticket_list (scope, tenant_id, id, title, version)
                VALUES (@scope, @tenantId, @id, @title, @version)
                ON CONFLICT (scope, tenant_id, id) DO UPDATE
                    SET title = EXCLUDED.title, version = EXCLUDED.version
                    WHERE app.ticket_list.version < EXCLUDED.version
                """;
            cmd.Parameters.AddWithValue("title", current.Document.Title);
            cmd.Parameters.AddWithValue("version", current.Version);
        }

        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
```

Why each line is there:

- **`SetScopeAsync(tx, change.Scope)` per change.** The feed carries every
  tenant's changes; the handler takes on the scope of the one it handles. With
  `scope_writable` in the policy, a bug that writes the wrong `tenant_id` fails
  with an RLS violation instead of landing in another tenant. `All` would not
  help here — it reads everything and writes nothing, by design.
- **Its own transaction.** The scope settings are transaction-local; the
  handler does not share the kernel's write transaction, so it opens its own.
  `SetScopeAsync` requires the transaction for exactly that reason.
- **Upsert with a version guard, delete as a no-op when absent.** Delivery is
  at-least-once and a rebuild replays everything (ADR-009, concepts §19); both
  statements are idempotent, and an older replay never overwrites a newer row.

## 3. Reading

```csharp
await using var conn = await appData.OpenConnectionAsync(ct);
await using var tx = await conn.BeginTransactionAsync(ct);
await conn.SetScopeAsync(tx, scope, ct);

await using var cmd = conn.CreateCommand();
cmd.Transaction = tx;
cmd.CommandText = "SELECT id, title FROM app.ticket_list ORDER BY title";   // no tenant filter needed
```

Add the `WHERE tenant_id = @tenantId` anyway where it helps the planner —
layer 1 of concepts §23. RLS is the layer that holds when you forget it.

## Where this does not protect you

- **Roles that skip RLS.** Superusers and roles with `BYPASSRLS` see everything.
  Run the application as a plain login role; keep the owner for migrations.
  `FORCE ROW LEVEL SECURITY` makes the policy apply to the table owner as well.
- **Outside PostgreSQL.** Search engines, vector stores and caches know nothing
  about RLS — the tenant goes into the key or a mandatory filter there
  ([external read models](external-read-models.md), rule 4).
- **Batching.** One transaction per change costs a round trip each. A
  high-volume projection can group consecutive changes of the same scope into
  one transaction — set the scope again whenever it changes.
