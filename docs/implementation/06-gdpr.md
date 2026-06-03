# 06 – DSGVO: Redaktion im Change Feed

## Das Problem

Event Sourcing und DSGVO scheinen sich direkt zu widersprechen:
- Events sind unveränderlich (Source of Truth)
- DSGVO fordert das Recht auf Löschung (Art. 17 DSGVO)

Dieses System löst den Widerspruch durch **explizite Redaktion** – keine stille Mutation, sondern ein transparenter, nachvollziehbarer Prozess.

---

## Drei gängige Ansätze und warum hier Redaktion gewählt wurde

| Ansatz | Wie es funktioniert | Nachteil |
|---|---|---|
| **Tombstone Events** | Neues "Gelöscht"-Event wird angehängt | Personendaten sind noch im alten Event sichtbar |
| **Encryption + Key Deletion** | Payload wird verschlüsselt, bei Löschung wird der Key gelöscht | Komplex, kryptografischer Aufwand, Recovery schwierig |
| **Redaction** | Payload wird auf Nulldaten gesetzt, `redacted = TRUE` | Einfach, explizit, auditierbar ← **gewählt** |

Redaktion passt am besten zu diesem System, weil:
- Die Event-Existenz bleibt erhalten (Audit: "Es gab einen User mit dieser ID")
- Der personenbezogene Inhalt wird gelöscht
- Es ist transparent und direkt debugbar
- Kein kryptografischer Overhead

---

## Das `redacted`-Flag in der Datenbank

Das Schema enthält bereits `redacted BOOLEAN DEFAULT FALSE` in `change_feed`. Der `ProjectionWorker` ignoriert redacted Events automatisch:

```sql
WHERE sequence_id > @lastSeen
  AND redacted = FALSE
```

Mit Scope-Modell gilt zusaetzlich:

- `scope` ist explizit (`Platform` oder `Tenant`).
- `tenant_id` ist nur bei `Scope = Tenant` gesetzt.
- GDPR-Operationen laufen immer in einem expliziten `ScopeContext`.

---

## Der `GdprProcessor`

### Sicherheitsanforderungen (Pflicht)

Der `GdprProcessor` hat Zugriff auf **alle personenbezogenen Daten** im System. Deshalb gelten folgende Regeln:

| Anforderung | Begründung |
|---|---|
| **`actorId` ist Pflichtparameter** | Jede Redaktion muss einem Akteur zugeordnet sein (wer hat die Löschung durchgeführt?) |
| **`reason` ist Pflichtparameter** | Nachvollziehbarkeit: DSGVO-Antrag, Ticket-Nummer, rechtliche Grundlage |
| **Jede Redaktion wird als Business Event protokolliert** | Audit-Trail der Löschung selbst |
| **`business_event_log` wird mitredacted** | Business Events können personenbezogene Daten enthalten (z.B. IP in `UserLoggedIn`) |
| **Change Feed + Business Event Log in einer Transaktion** | Keine Teilredaktion bei Crash |

> ⚠️ **Der `GdprProcessor` darf nur über autorisierte Endpoints aufgerufen werden.** Rate-Limiting ist empfohlen, um Missbrauch (massenhafte Redaktion als DoS-Vektor) zu verhindern.

```csharp
// src/Kernel/Gdpr/GdprProcessor.cs
public class GdprProcessor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly BusinessEventWriter _businessEventWriter;
    private readonly ILogger<GdprProcessor> _logger;

    public GdprProcessor(
        NpgsqlDataSource dataSource,
        BusinessEventWriter businessEventWriter,
        ILogger<GdprProcessor> logger)
    {
        _dataSource = dataSource;
        _businessEventWriter = businessEventWriter;
        _logger = logger;
    }

    /// <summary>
    /// Redacts all Change Feed AND Business Event Log entries for a specific entity.
    /// Both tables are redacted in ONE transaction. The redaction itself is logged as a Business Event.
    /// </summary>
    public async Task<RedactionResult> RedactEntityAsync(
        ScopeContext scope,
        string entity,
        string entityId,
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("actorId is required for GDPR redaction.", nameof(actorId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required for GDPR redaction.", nameof(reason));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. Change Feed redacten
        await using var feedCmd = conn.CreateCommand();
        feedCmd.Transaction = tx;
        feedCmd.CommandText = """
            UPDATE change_feed
            SET payload  = '{"redacted": true}'::jsonb,
                redacted = TRUE
                        WHERE scope     = @scope
                            AND (
                                     (@tenantId IS NULL AND tenant_id IS NULL)
                                     OR
                                     tenant_id = @tenantId
                            )
                            AND entity    = @entity
              AND entity_id = @entityId
              AND redacted  = FALSE
            """;
                feedCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
                feedCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        feedCmd.Parameters.AddWithValue("entity", entity);
        feedCmd.Parameters.AddWithValue("entityId", entityId);
        var feedAffected = await feedCmd.ExecuteNonQueryAsync(ct);

        // 2. Business Event Log redacten
        await using var belCmd = conn.CreateCommand();
        belCmd.Transaction = tx;
        belCmd.CommandText = """
            UPDATE business_event_log
            SET payload  = '{"redacted": true}'::jsonb,
                redacted = TRUE
                        WHERE scope     = @scope
                            AND (
                                     (@tenantId IS NULL AND tenant_id IS NULL)
                                     OR
                                     tenant_id = @tenantId
                            )
                            AND entity    = @entity
              AND entity_id = @entityId
              AND redacted  = FALSE
            """;
                belCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
                belCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        belCmd.Parameters.AddWithValue("entity", entity);
        belCmd.Parameters.AddWithValue("entityId", entityId);
        var belAffected = await belCmd.ExecuteNonQueryAsync(ct);

        // 3. Redaktion selbst als Business Event protokollieren (Audit der Löschung)
        var auditPayload = JsonSerializer.Serialize(new
        {
            RedactedEntity = entity,
            RedactedEntityId = entityId,
            Reason = reason,
            FeedEventsRedacted = feedAffected,
            BusinessEventsRedacted = belAffected,
            RedactedAt = DateTimeOffset.UtcNow
        });

        await _businessEventWriter.AppendAsync(
            transaction: tx,
            scope: scope,
            eventType: "EntityRedacted",
            actorId: actorId,
            payloadJson: auditPayload,
            ct: ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "GDPR redaction completed: entity={Entity}, entityId={EntityId}, actor={ActorId}, " +
            "feedEvents={FeedAffected}, businessEvents={BelAffected}, reason={Reason}",
            entity, entityId, actorId, feedAffected, belAffected, reason);

        return new RedactionResult(feedAffected, belAffected);
    }

    /// <summary>
    /// Returns all change feed entries for a specific entity (for GDPR Art. 15 – right of access).
    /// Includes both Change Feed and Business Event Log entries.
    /// </summary>
    public async Task<EntityHistory> GetEntityHistoryAsync(
        ScopeContext scope,
        string entity,
        string entityId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Change Feed History
        await using var feedCmd = conn.CreateCommand();
        feedCmd.CommandText = """
             SELECT sequence_id, entity, entity_id, event_type, version,
                 correlation_id, causation_id, actor_id, payload::text, timestamp, scope, tenant_id
            FROM change_feed
             WHERE scope     = @scope
            AND (
                 (@tenantId IS NULL AND tenant_id IS NULL)
                 OR
                 tenant_id = @tenantId
            )
            AND entity    = @entity
              AND entity_id = @entityId
            ORDER BY sequence_id
            """;
         feedCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
         feedCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        feedCmd.Parameters.AddWithValue("entity", entity);
        feedCmd.Parameters.AddWithValue("entityId", entityId);

        var changeRecords = new List<ChangeRecord>();
        await using var feedReader = await feedCmd.ExecuteReaderAsync(ct);
        while (await feedReader.ReadAsync(ct))
        {
            changeRecords.Add(new ChangeRecord(
                SequenceId:    feedReader.GetInt64(0),
                Entity:        feedReader.GetString(1),
                EntityId:      feedReader.GetString(2),
                EventType:     feedReader.GetString(3),
                Version:       feedReader.GetInt32(4),
                CorrelationId: feedReader.IsDBNull(5) ? null : feedReader.GetString(5),
                CausationId:   feedReader.IsDBNull(6) ? null : feedReader.GetString(6),
                ActorId:       feedReader.GetString(7),
                PayloadJson:   feedReader.GetString(8),
                Timestamp:     feedReader.GetFieldValue<DateTimeOffset>(9),
                Scope:         Enum.Parse<ScopeType>(feedReader.GetString(10), ignoreCase: false),
                TenantId:      feedReader.IsDBNull(11) ? null : feedReader.GetString(11)
            ));
        }

        // Business Event Log History
        await using var belCmd = conn.CreateCommand();
        belCmd.CommandText = """
                        SELECT event_id, event_type, actor_id, payload::text, occurred_at, scope, tenant_id
            FROM business_event_log
                        WHERE scope     = @scope
                            AND (
                                     (@tenantId IS NULL AND tenant_id IS NULL)
                                     OR
                                     tenant_id = @tenantId
                            )
                            AND entity    = @entity
              AND entity_id = @entityId
            ORDER BY occurred_at
            """;
                belCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
                belCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        belCmd.Parameters.AddWithValue("entity", entity);
        belCmd.Parameters.AddWithValue("entityId", entityId);

        var businessEvents = new List<BusinessEventRecord>();
        await using var belReader = await belCmd.ExecuteReaderAsync(ct);
        while (await belReader.ReadAsync(ct))
        {
            businessEvents.Add(new BusinessEventRecord(
                EventId:     belReader.GetGuid(0),
                EventType:   belReader.GetString(1),
                ActorId:     belReader.GetString(2),
                PayloadJson: belReader.GetString(3),
                OccurredAt:  belReader.GetFieldValue<DateTimeOffset>(4),
                Scope:       Enum.Parse<ScopeType>(belReader.GetString(5), ignoreCase: false),
                TenantId:    belReader.IsDBNull(6) ? null : belReader.GetString(6)
            ));
        }

        return new EntityHistory(changeRecords, businessEvents);
    }
}

// Ergebnis-Typen
public record RedactionResult(int FeedEventsRedacted, int BusinessEventsRedacted);
public record BusinessEventRecord(
    Guid EventId,
    string EventType,
    string ActorId,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    ScopeType Scope,
    string? TenantId);
public record EntityHistory(List<ChangeRecord> ChangeRecords, List<BusinessEventRecord> BusinessEvents);
```

**Wichtige Änderungen gegenüber der Minimalversion:**

1. **`actorId` und `reason` sind Pflichtparameter** – Nachvollziehbarkeit der Löschung
2. **`business_event_log` wird mitredacted** – keine DSGVO-Lücke bei personenbezogenen Business Events
3. **Alles in einer Transaktion** – keine Teilredaktion bei Crash
4. **Redaktion wird als Business Event protokolliert** – Audit-Trail der Löschung selbst
5. **`GetEntityHistoryAsync` liest beide Tabellen** – vollständiges Bild für Art. 15 Auskunft
6. **`ChangeRecord` wird mit allen Feldern gelesen** – konsistent mit der Definition in `03-change-feed.md`

---

## DSGVO-Anforderungen und wie sie erfüllt werden

### Art. 17 – Recht auf Löschung ("Recht auf Vergessenwerden")

```csharp
// Wenn ein User die Löschung beantragt:
var result = await gdprProcessor.RedactEntityAsync(
    scope:   ScopeContext.Tenant("acme"),
    entity:   "User",
    entityId: userId.ToString(),
    actorId:  "admin:current-admin-id",       // Pflicht: wer führt die Löschung durch?
    reason:   "DSGVO Art. 17, Ticket #12345", // Pflicht: warum?
    ct:       ct);

// result.FeedEventsRedacted = Anzahl redacted Events im Change Feed
// result.BusinessEventsRedacted = Anzahl redacted Events im Business Event Log
```

Nach der Redaktion:
- Alle Feed-Einträge für diesen User haben `redacted = TRUE` (in **beiden** Tabellen)
- Der Payload ist durch `{"redacted": true}` ersetzt
- Die Redaktion selbst ist als `EntityRedacted`-Event im Business Event Log protokolliert
- Projections ignorieren redacted Events beim nächsten Rebuild
- Die Sequenz-IDs bleiben erhalten (kein Loch in der Timeline, kein referenzieller Schaden)

### Art. 15 – Recht auf Auskunft

```csharp
var history = await gdprProcessor.GetEntityHistoryAsync(
    ScopeContext.Tenant("acme"),
    "User",
    userId.ToString(),
    ct);
// history.ChangeRecords = alle Change-Feed-Einträge (inkl. redacted)
// history.BusinessEvents = alle Business-Event-Log-Einträge (inkl. redacted)
```

> **Beachte:** Art. 15 erfordert die Auskunft über **alle** gespeicherten Daten. Deshalb liefert `GetEntityHistoryAsync` sowohl Change Feed als auch Business Event Log.

### Art. 5 Abs. 1 lit. e – Speicherbegrenzung (Retention Policy)

Events, die nicht mehr benötigt werden, können nach einer definierten Aufbewahrungsfrist redacted werden. Eine Retention Policy sollte als **Betriebsinvariante** definiert werden:

```sql
-- Retention: Events älter als 3 Jahre für gelöschte User redacten
-- Beide Tabellen berücksichtigen!

-- Change Feed
UPDATE change_feed
SET payload = '{"redacted": true}'::jsonb, redacted = TRUE
WHERE timestamp < NOW() - INTERVAL '3 years'
  AND redacted = FALSE
  AND entity_id IN (
      SELECT id::text FROM deleted_users
  );

-- Business Event Log
UPDATE business_event_log
SET payload = '{"redacted": true}'::jsonb, redacted = TRUE
WHERE occurred_at < NOW() - INTERVAL '3 years'
  AND redacted = FALSE
  AND entity_id IN (
      SELECT id::text FROM deleted_users
  );
```

> **Empfehlung für große Systeme:** PostgreSQL Table Partitioning nach `timestamp`/`occurred_at` (monatlich oder jährlich). Damit können alte Partitionen effizient archiviert oder gelöscht werden, ohne den aktiven Datenbestand zu belasten.

---

## Was nach einer Redaktion passiert

1. Der Worker verarbeitet redacted Events nicht mehr (Filter im SQL)
2. Bestehende Read Models in Projections **müssen** separat bereinigt werden
3. Beim nächsten Replay werden redacted Events automatisch übersprungen

> ⚠️ **Wichtig: Laufende Projections und Redaktion**
>
> Wenn eine Projection gerade Events für einen User verarbeitet, hat sie den **originalen Payload** bereits im Speicher geladen. Die Redaktion im Change Feed hat keinen Effekt auf bereits geladene Events. Deshalb ist die Read-Model-Bereinigung nach einer Redaktion **Pflicht**, nicht optional.

### Read Model nach Redaktion bereinigen (Pflicht)

Die Bereinigung der Read Models ist Aufgabe der jeweiligen Feature-Schicht, nicht des Kernels. Der vollständige Löschprozess muss **so atomar wie möglich** sein:

```csharp
// src/App/Features/Users/UserDeletionService.cs
public class UserDeletionService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly GdprProcessor _gdprProcessor;
    private readonly ReplayService _replayService;

    public async Task DeleteUserAsync(
        Guid userId,
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        // 1. Domain-Daten löschen + Change Feed/Business Event Log redacten
        //    (GdprProcessor macht beides in einer Transaktion)
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Domain-Löschung in derselben Transaktion wie die Redaktion
        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = tx;
        deleteCmd.CommandText = "DELETE FROM users WHERE id = @id";
        deleteCmd.Parameters.AddWithValue("id", userId);
        await deleteCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);

        // 2. Feed-Redaktion (eigene Transaktion im GdprProcessor)
        var result = await _gdprProcessor.RedactEntityAsync(
            "User", userId.ToString(), actorId, reason, ct);

        // 3. Read Models EXPLIZIT bereinigen (PFLICHT, nicht optional)
        await DeleteFromSearchIndex(userId, ct);
        await DeleteFromAnalytics(userId, ct);

        // 4. Replay der betroffenen Projections anfordern (EMPFOHLEN)
        //    Stellt sicher, dass auch Events, die zwischen Redaktion und
        //    Bereinigung verarbeitet wurden, korrekt behandelt werden.
        await _replayService.RequestReplayAsync("user_read_model", ct);
        await _replayService.RequestReplayAsync("user_search_index", ct);
    }
}
```

> **Empfehlung:** Für kritische DSGVO-Löschungen ist der Replay der betroffenen Projections **nicht optional, sondern empfohlen**. Das stellt sicher, dass auch Events, die zwischen Redaktion und Bereinigung verarbeitet wurden, korrekt behandelt werden.

---

## Was NICHT gemacht wird

- **Kein Soft Delete** im Domain-Modell als DSGVO-Ersatz – Soft Delete lässt Personendaten im System
- **Keine Verschlüsselung** als primäre Strategie – erhöht Komplexität ohne wesentlichen Vorteil für dieses System
- **Kein automatisches Löschen aus Projections** durch den Kernel – das ist Feature-Verantwortung

---

## Wichtige Erkenntnis

DSGVO-Compliance muss **explizit und testbar** sein. Ein KI-generierter Layer, der "irgendwie" Löschung abstrahiert, ist hier nicht akzeptabel. Jeder Schritt ist sichtbar, nachvollziehbar und im Code direkt auffindbar.

### Checkliste für DSGVO-Löschung

Jede Löschung muss folgende Schritte durchlaufen:

- [ ] Domain-Daten aus CRUD-Tabellen gelöscht
- [ ] Change Feed für die Entity redacted (`redacted = TRUE`, Payload ersetzt)
- [ ] Business Event Log für die Entity redacted (`redacted = TRUE`, Payload ersetzt)
- [ ] Redaktion als `EntityRedacted`-Event protokolliert (mit `actorId` und `reason`)
- [ ] Alle Read Models explizit bereinigt
- [ ] Replay der betroffenen Projections angefordert
- [ ] Löschung dokumentiert (Audit-Log, Ticket-Referenz)

### Testbarkeit

Jeder Schritt der Checkliste sollte durch einen Integrationstest abgedeckt sein:

```csharp
[Fact]
public async Task DeleteUser_RedactsAllPersonalData()
{
    // Arrange: User anlegen, Events erzeugen
    // Act: DeleteUserAsync aufrufen
    // Assert:
    //   - users-Tabelle: kein Eintrag mehr
    //   - change_feed: alle Events für diesen User haben redacted = TRUE
    //   - business_event_log: alle Events für diesen User haben redacted = TRUE
    //   - EntityRedacted-Event existiert im business_event_log
    //   - Read Models: keine Daten mehr für diesen User
}
```
