# Recipe: AI consumers of the change feed

Status: verified against the implemented API (phase 13, 2026-06-12)

Stance (phase 13): **the kernel stays AI-free** — no LLM calls, no AI
dependencies, deterministic infrastructure. But it is deliberately
AI-*friendly*: the policy-minimized feed is safe reading material, scope binding
is a natural permission boundary, and dumb handlers (ADR-009) are the universal
attachment point. Everything here is an **application recipe**, not a kernel
feature.

## 1. Embeddings/RAG: a pgvector index as a projection

Semantic search over documents — the embeddings index is a perfectly normal
projection: a change handler that, on relevant changes, loads the document,
computes an embedding and writes it into a pgvector table (same Postgres
instance!). Rebuild, checkpoints, lag metrics — all for free (concepts §19).

```sql
CREATE EXTENSION IF NOT EXISTS vector;
CREATE TABLE app.product_embedding (
    scope text NOT NULL, tenant_id text NOT NULL, document_id text NOT NULL,
    version bigint NOT NULL,                  -- idempotency: only write newer
    embedding vector(1536) NOT NULL,
    PRIMARY KEY (scope, tenant_id, document_id)
);
-- Same tenant isolation as papuma.* (ADR-019):
ALTER TABLE app.product_embedding ENABLE ROW LEVEL SECURITY;
ALTER TABLE app.product_embedding FORCE ROW LEVEL SECURITY;
CREATE POLICY scope_isolation ON app.product_embedding
    USING (papuma.scope_visible(scope, tenant_id))
    WITH CHECK (papuma.scope_writable(scope, tenant_id));
-- DELETE is checked against USING only: without this the All scope could delete
CREATE POLICY scope_delete ON app.product_embedding AS RESTRICTIVE FOR DELETE
    USING (papuma.scope_writable(scope, tenant_id));
```

```csharp
public sealed class ProductEmbeddingProjection(
    DocumentStore store, NpgsqlDataSource dataSource, IEmbeddingClient embeddings)
    : IChangeHandler, IProjection
{
    public string Name => "product-embeddings";   // checkpoint identity
    public int Version => 1;                      // raise it to re-embed everything (ADR-024)

    public async Task ResetAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("TRUNCATE app.product_embedding");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != "Product") return;
        if (change.Operation == ChangeOperation.Delete) { /* DELETE embedding row */ return; }
        if (!change.FieldChanged("name") && !change.FieldChanged("description")) return;

        // Load the state (the diff only carries changes) — scope-bound!
        await using var session = store.OpenSession(change.Scope);
        var product = await session.LoadAsync<Product>(change.DocumentId, ct);
        if (product is null || product.Version > change.Version) return; // stale: a later
            // change recomputes anyway — idempotency via the version column (see below)

        var vector = await embeddings.EmbedAsync($"{product.Document.Name}\n{product.Document.Description}", ct);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app.product_embedding (scope, tenant_id, document_id, version, embedding)
            VALUES (@scope, @tenant, @id, @version, @embedding)
            ON CONFLICT (scope, tenant_id, document_id)
            DO UPDATE SET version = @version, embedding = @embedding
            WHERE app.product_embedding.version < @version
            """;
        // bind parameters … (at-least-once: the version predicate makes redelivery harmless)
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

Notes: embedding API calls are slow → this handler dominates
`papuma.feed.handler.duration`; when lag grows, look here first (concepts §14)
and consider writing only a marker document + separate batch processing. The RAG
query itself is ordinary SQL (`ORDER BY embedding <=> @query`) with explicit
scope predicates. With the policy above, the handler's write needs the change's
scope set on its own transaction (`SetScopeAsync(tx, change.Scope)`), and the
query runs under the reader's scope — the full pattern, verified against
PostgreSQL 18, is the [same-database read-model recipe](same-database-read-models.md).

## 2. Natural-language audit: `GetHistoryAsync` + LLM

"What happened to order 4711?" — the history is policy-applied (sensitive values
never reach the LLM) and carries actor/correlation/time:

```csharp
public async Task<string> ExplainHistoryAsync(string orderId, ScopeContext scope, CancellationToken ct)
{
    await using var session = _store.OpenSession(scope);
    IReadOnlyList<ChangeRecord> history = await session.GetHistoryAsync<Order>(orderId, ct: ct);

    var facts = history.Select(c => new
    {
        c.Version, Operation = c.Operation.ToString(), c.OccurredAt,
        Actor = (string?)c.Metadata["actorId"],
        Changes = c.Diff.ToJson(),       // redacted fields: only {"changed": true}
    });

    return await _llm.CompleteAsync($"""
        Explain to a support agent, chronologically and briefly, what happened
        to this order. Fields with {{"changed": true}} are protected — only
        mention THAT they changed.

        {JsonSerializer.Serialize(facts)}
        """, ct);
}
```

The same pattern works via the MCP server (`get_document_history`): an agent with
access to the tool answers such questions without custom code — policy
application holds identically there.

## 3. Anomaly detection: the feed as a behavior stream

The event feed is a chronological fact stream per scope — ideal fodder for
detection logic (rule-based or model-based). Again, just a handler:

```csharp
[StartsAtFeedHead] // an effect: added later, it must not flag years of past logins
public sealed class LoginAnomalyDetector(DocumentStore store) : IEventHandler
{
    public string Name => "login-anomaly-detector";

    public async Task HandleAsync(EventRecord @event, CancellationToken ct)
    {
        if (@event.EventType != nameof(UserLoggedIn)) return;
        var login = @event.Deserialize<UserLoggedIn>();

        if (!await _detector.IsSuspiciousAsync(login, ct)) return;

        // The finding = a document (human-in-the-loop pattern, concepts §18):
        // a deterministic id makes at-least-once harmless.
        await using var session = store.OpenSession(@event.Scope);
        await session.SaveAsync(new SecurityAlert(
            Id: $"alert-login-{@event.Seq}", UserId: login.UserId,
            Status: AlertStatus.Open, RaisedAt: @event.OccurredAt), 0);
        try { await session.CommitAsync(); }
        catch (UniqueKeyViolationException) { /* redelivery — alert exists */ }
    }
}
```

The alert is itself a document → triage by a human (or an agent) is a normal
write that the next handler reacts to — the entire escalation chain runs on
kernel primitives.

## Guardrails for all three patterns

1. **LLM/embedding calls belong in handlers or application code, never in the
   write path** — they are slow and nondeterministic; the feed decouples.
2. **Policy discipline is the AI safety boundary**: what is redacted cannot be
   leaked by any prompt. Before the first AI consumer, review the inventory
   (`DataInventory` → `UnprotectedPaths`, [gdpr.md](../gdpr.md)).
3. **Scope binding is the permission boundary**: an agent/handler works with the
   scope of the triggering record — never aggregate across scopes, except
   deliberately in an `'All'` worker.
4. **Idempotency as always** (at-least-once): version predicates, deterministic
   ids, unique keys.
