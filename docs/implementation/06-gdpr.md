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

---

## Der `GdprProcessor`

```csharp
// src/Kernel/Gdpr/GdprProcessor.cs
public class GdprProcessor
{
    private readonly NpgsqlDataSource _dataSource;

    public GdprProcessor(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Redacts all Change Feed entries for a specific entity.
    /// The event records remain (for audit), but payload is cleared.
    /// </summary>
    public async Task RedactEntityAsync(
        string entity,
        string entityId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE change_feed
            SET payload  = '{"redacted": true}'::jsonb,
                redacted = TRUE
            WHERE entity    = @entity
              AND entity_id = @entityId
            """;

        cmd.Parameters.AddWithValue("entity",   entity);
        cmd.Parameters.AddWithValue("entityId", entityId);

        var affected = await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns all change feed entries for a specific entity (for GDPR Art. 15 – right of access).
    /// </summary>
    public async Task<List<ChangeRecord>> GetEntityHistoryAsync(
        string entity,
        string entityId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT sequence_id, entity, entity_id, event_type, version,
                   payload::text, timestamp
            FROM change_feed
            WHERE entity    = @entity
              AND entity_id = @entityId
            ORDER BY sequence_id
            """;

        cmd.Parameters.AddWithValue("entity",   entity);
        cmd.Parameters.AddWithValue("entityId", entityId);

        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                SequenceId:  reader.GetInt64(0),
                Entity:      reader.GetString(1),
                EntityId:    reader.GetString(2),
                EventType:   reader.GetString(3),
                Version:     reader.GetInt32(4),
                PayloadJson: reader.GetString(5),
                Timestamp:   reader.GetDateTime(6)
            ));
        }

        return records;
    }
}
```

---

## DSGVO-Anforderungen und wie sie erfüllt werden

### Art. 17 – Recht auf Löschung ("Recht auf Vergessenwerden")

```csharp
// Wenn ein User die Löschung beantragt:
await gdprProcessor.RedactEntityAsync("User", userId.ToString(), ct);

// Außerdem: den User selbst aus der Domain-Tabelle löschen
// (das ist normales CRUD, kein Kernel-Thema)
```

Nach der Redaktion:
- Alle Feed-Einträge für diesen User haben `redacted = TRUE`
- Der Payload ist durch `{"redacted": true}` ersetzt
- Projections ignorieren redacted Events beim nächsten Rebuild
- Die Sequenz-IDs bleiben erhalten (kein Loch in der Timeline, kein referenzieller Schaden)

### Art. 15 – Recht auf Auskunft

```csharp
var history = await gdprProcessor.GetEntityHistoryAsync("User", userId.ToString(), ct);
// Liefert alle (noch nicht redigierten) Events für diesen User
```

### Art. 5 Abs. 1 lit. e – Speicherbegrenzung

Events, die nicht mehr benötigt werden, können nach einer definierten Aufbewahrungsfrist redacted werden – z.B. via Scheduled Job:

```sql
-- Events älter als 3 Jahre für gelöschte User redacten
UPDATE change_feed
SET payload = '{"redacted": true}'::jsonb, redacted = TRUE
WHERE timestamp < NOW() - INTERVAL '3 years'
  AND entity_id IN (
      SELECT id::text FROM deleted_users
  );
```

---

## Was nach einer Redaktion passiert

1. Der Worker verarbeitet redacted Events nicht mehr (Filter im SQL)
2. Bestehende Read Models in Projections müssen separat bereinigt werden
3. Beim nächsten Replay werden redacted Events automatisch übersprungen

### Read Model nach Redaktion bereinigen

Das ist Aufgabe der jeweiligen Feature-Schicht, nicht des Kernels:

```csharp
// src/App/Features/Users/UserDeletionService.cs
public async Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
{
    // 1. Domain-Daten löschen
    await DeleteFromUsersTable(userId, ct);

    // 2. Change Feed redacten
    await _gdprProcessor.RedactEntityAsync("User", userId.ToString(), ct);

    // 3. Read Models explizit bereinigen (optional: via Projection Replay)
    await DeleteFromSearchIndex(userId, ct);
    await DeleteFromAnalytics(userId, ct);
}
```

---

## Was NICHT gemacht wird

- **Kein Soft Delete** im Domain-Modell als DSGVO-Ersatz – Soft Delete lässt Personendaten im System
- **Keine Verschlüsselung** als primäre Strategie – erhöht Komplexität ohne wesentlichen Vorteil für dieses System
- **Kein automatisches Löschen aus Projections** durch den Kernel – das ist Feature-Verantwortung

---

## Wichtige Erkenntnis

DSGVO-Compliance muss **explizit und testbar** sein. Ein KI-generierter Layer, der "irgendwie" Löschung abstrahiert, ist hier nicht akzeptabel. Jeder Schritt ist sichtbar, nachvollziehbar und im Code direkt auffindbar.
