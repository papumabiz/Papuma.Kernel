# Rezept: KI-Konsumenten des Change Feeds

Status: Verifiziert gegen die implementierte API (Phase 13, 2026-06-12)

Haltung (Phase 13): **Der Kernel bleibt KI-frei** — keine LLM-Aufrufe, keine
KI-Abhängigkeiten, deterministische Infrastruktur. Aber er ist bewusst
KI-*freundlich*: Der policy-minimierte Feed ist sicherer Lesestoff, die
Scope-Bindung eine natürliche Berechtigungsgrenze, und dumme Handler (ADR-009)
sind der universelle Andockpunkt. Alles hier sind **Anwendungsrezepte**, keine
Kernel-Features.

## 1. Embeddings/RAG: pgvector-Index als Projektion

Semantische Suche über Dokumente — der Embeddings-Index ist eine ganz normale
Projektion: ein Change-Handler, der bei relevanten Änderungen das Dokument lädt,
ein Embedding rechnet und in eine pgvector-Tabelle (gleiche Postgres-Instanz!)
schreibt. Rebuild, Checkpoints, Lag-Metriken — alles geschenkt (concepts §19).

```sql
CREATE EXTENSION IF NOT EXISTS vector;
CREATE TABLE app.product_embedding (
    scope text NOT NULL, tenant_id text NOT NULL, document_id text NOT NULL,
    version bigint NOT NULL,                  -- Idempotenz: nur neuere schreiben
    embedding vector(1536) NOT NULL,
    PRIMARY KEY (scope, tenant_id, document_id)
);
```

```csharp
public sealed class ProductEmbeddingProjection(
    DocumentStore store, NpgsqlDataSource dataSource, IEmbeddingClient embeddings)
    : IChangeHandler
{
    public string Name => "product-embeddings";   // Checkpoint-Identität

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != "Product") return;
        if (change.Operation == ChangeOperation.Delete) { /* DELETE embedding row */ return; }
        if (!change.FieldChanged("name") && !change.FieldChanged("description")) return;

        // Zustand laden (der Diff trägt nur Änderungen) — scope-gebunden!
        await using var session = store.OpenSession(change.Scope);
        var product = await session.LoadAsync<Product>(change.DocumentId, ct);
        if (product is null || product.Version > change.Version) return; // stale: späterer
            // Change rechnet ohnehin neu — Idempotenz über die version-Spalte (s. u.)

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
        // Parameter binden … (at-least-once: das version-Prädikat macht Redelivery harmlos)
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

Hinweise: Embedding-API-Aufrufe sind langsam → dieser Handler dominiert
`papuma.feed.handler.duration`; bei Lag-Wachstum zuerst hier schauen (concepts
§14) und ggf. nur ein Marker-Dokument schreiben + Batch-Verarbeitung getrennt.
Die RAG-Abfrage selbst ist gewöhnliches SQL (`ORDER BY embedding <=> @query`)
mit expliziten Scope-Prädikaten.

## 2. Natural-Language-Audit: `GetHistoryAsync` + LLM

"Was ist mit Bestellung 4711 passiert?" — die Historie ist policy-bereinigt
(sensible Werte erreichen das LLM nie) und trägt Actor/Correlation/Zeit:

```csharp
public async Task<string> ExplainHistoryAsync(string orderId, ScopeContext scope, CancellationToken ct)
{
    await using var session = _store.OpenSession(scope);
    IReadOnlyList<ChangeRecord> history = await session.GetHistoryAsync<Order>(orderId, ct: ct);

    var facts = history.Select(c => new
    {
        c.Version, Operation = c.Operation.ToString(), c.OccurredAt,
        Actor = (string?)c.Metadata["actorId"],
        Changes = c.Diff.ToJson(),       // Redacted-Felder: nur {"changed": true}
    });

    return await _llm.CompleteAsync($"""
        Erkläre einem Support-Mitarbeiter chronologisch und knapp, was mit dieser
        Bestellung passiert ist. Felder mit {{"changed": true}} sind geschützt —
        erwähne nur, DASS sie sich geändert haben.

        {JsonSerializer.Serialize(facts)}
        """, ct);
}
```

Dasselbe Muster über den MCP-Server (`get_document_history`): ein Agent mit
Zugriff auf das Tool beantwortet solche Fragen ohne eigenen Code — die
Policy-Bereinigung gilt dort identisch.

## 3. Anomalie-Erkennung: der Feed als Verhaltensstrom

Der Event-Feed ist ein chronologischer Faktenstrom pro Scope — ideales Futter
für Erkennungslogik (regelbasiert oder Modell). Wieder nur ein Handler:

```csharp
public sealed class LoginAnomalyDetector(DocumentStore store) : IEventHandler
{
    public string Name => "login-anomaly-detector";

    public async Task HandleAsync(EventRecord @event, CancellationToken ct)
    {
        if (@event.EventType != nameof(UserLoggedIn)) return;
        var login = @event.Deserialize<UserLoggedIn>();

        if (!await _detector.IsSuspiciousAsync(login, ct)) return;

        // Befund = Dokument (Human-in-the-Loop-Muster, concepts §18):
        // deterministische Id macht at-least-once harmlos.
        await using var session = store.OpenSession(@event.Scope);
        await session.SaveAsync(new SecurityAlert(
            Id: $"alert-login-{@event.Seq}", UserId: login.UserId,
            Status: AlertStatus.Open, RaisedAt: @event.OccurredAt), 0);
        try { await session.CommitAsync(); }
        catch (UniqueKeyViolationException) { /* Redelivery — Alert existiert */ }
    }
}
```

Der Alert ist selbst ein Dokument → die Triage durch einen Menschen (oder
Agenten) ist ein normaler Write, der nächste Handler reagiert darauf — die
komplette Eskalationskette läuft auf Kernel-Primitiven.

## Leitplanken für alle drei Muster

1. **LLM-/Embedding-Aufrufe gehören in Handler oder Anwendungscode, nie in den
   Write-Pfad** — sie sind langsam und nichtdeterministisch; der Feed entkoppelt.
2. **Policy-Disziplin ist die KI-Sicherheitsgrenze**: Was redacted ist, kann kein
   Prompt leaken. Vor dem ersten KI-Konsumenten das Inventar prüfen
   (`DataInventory` → `UnprotectedPaths`, [gdpr.md](../gdpr.md)).
3. **Scope-Bindung ist die Berechtigungsgrenze**: Ein Agent/Handler arbeitet mit
   dem Scope des auslösenden Records — nie scope-übergreifend aggregieren, außer
   bewusst im `'All'`-Worker.
4. **Idempotenz wie immer** (at-least-once): version-Prädikate, deterministische
   IDs, Unique-Keys.
