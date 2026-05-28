# 03 – Change Feed: ChangeRecord und ChangeWriter

## Das Datenmodell: `ChangeRecord`

`ChangeRecord` ist das zentrale Objekt des Systems. Es repräsentiert einen einzelnen Eintrag im Change Feed – also einen aufgezeichneten Zustandsübergang.

```csharp
// src/Kernel/ChangeFeed/ChangeRecord.cs
public record ChangeRecord(
    long     SequenceId,
    string   Entity,
    string   EntityId,
    string   EventType,
    int      Version,
    string   PayloadJson,
    DateTime Timestamp
);
```

**Warum ein `record`?**  
`record` ist in C# semantisch immutable und hat eingebaute value equality. Das passt perfekt zu einem Event, das niemals verändert werden soll.

**Warum `PayloadJson` als `string` und nicht als generisches `T`?**  
Weil der Kernel nicht wissen muss, was im Payload steckt. Das ist Aufgabe der Projection. Der Kernel speichert und transportiert – nicht mehr. Typisierung passiert bei der Interpretation, nicht im Speichermechanismus.

---

## Der `ChangeWriter`

Der `ChangeWriter` ist für das atomare Schreiben zuständig: CRUD-Mutation und Change Feed Append passieren in **einer** Datenbanktransaktion. Entweder beides, oder nichts.

```csharp
// src/Kernel/ChangeFeed/ChangeWriter.cs
public class ChangeWriter
{
    private readonly NpgsqlDataSource _dataSource;

    public ChangeWriter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task AppendAsync(
        NpgsqlTransaction transaction,
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        CancellationToken ct = default)
    {
        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO change_feed (entity, entity_id, event_type, version, payload)
            VALUES (@entity, @entityId, @eventType, @version, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("entity",    entity);
        cmd.Parameters.AddWithValue("entityId",  entityId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("version",   version);
        cmd.Parameters.AddWithValue("payload",   payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

---

## Wie eine CRUD-Operation mit Change Feed aussieht

Hier ein vollständiges Beispiel: E-Mail eines Users aktualisieren.

```csharp
// src/App/Features/Users/UpdateUserEmailHandler.cs
public class UpdateUserEmailHandler
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ChangeWriter     _changeWriter;

    public UpdateUserEmailHandler(NpgsqlDataSource dataSource, ChangeWriter changeWriter)
    {
        _dataSource   = dataSource;
        _changeWriter = changeWriter;
    }

    public async Task HandleAsync(Guid userId, string newEmail, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);

        // 1. CRUD Mutation
        await using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = """
            UPDATE users
            SET email = @email, updated_at = NOW()
            WHERE id = @id
            """;
        updateCmd.Parameters.AddWithValue("email", newEmail);
        updateCmd.Parameters.AddWithValue("id",    userId);
        await updateCmd.ExecuteNonQueryAsync(ct);

        // 2. Change Feed Append (in derselben Transaktion)
        var payload = JsonSerializer.Serialize(new { Email = newEmail });

        await _changeWriter.AppendAsync(
            transaction: tx,
            entity:      "User",
            entityId:    userId.ToString(),
            eventType:   "UserEmailUpdated",
            version:     1,
            payloadJson: payload,
            ct:          ct
        );

        // 3. Commit – atomarer Abschluss
        await tx.CommitAsync(ct);
    }
}
```

**Das ist der entscheidende Punkt:** Schritt 1 und 2 sind atomar. Es gibt keinen Zustand, in dem die E-Mail aktualisiert wurde, aber kein Feed-Eintrag existiert – und umgekehrt.

---

## Payload-Konventionen

### Versionierung

Wenn sich die Struktur eines Payloads ändert, wird `version` inkrementiert. Die alten Events bleiben unverändert im Feed.

**Version 1:**
```json
{ "Email": "alice@example.com" }
```

**Version 2** (E-Mail-Objekt mit Verifikationsstatus):
```json
{ "Email": { "Value": "alice@example.com", "Verified": true } }
```

Die Version 1-Events in der Datenbank werden **nicht angefasst**. Projections lernen, beide Versionen zu lesen (siehe [05-versionierung.md](05-versionierung.md)).

### Payload minimal halten

> Im Payload nur das speichern, was für die Interpretation des Events nötig ist.

Nicht den gesamten User-State, sondern nur was sich geändert hat. Das hält den Feed klein und Events semantisch klar.

---

## Payload-Typen definieren

Für die Serialisierung definiert man einfache Records:

```csharp
// src/App/Features/Users/Events/UserEmailUpdatedPayload.cs
public record UserEmailUpdatedPayload(string Email);
```

Oder mit Version:

```csharp
// V1
public record UserEmailUpdatedV1(string Email);

// V2
public record UserEmailUpdatedV2(string Value, bool Verified);
```

---

## Was der `ChangeWriter` bewusst NICHT macht

- Kein Routing an andere Services
- Kein Event-Dispatching
- Kein In-Memory-Bus
- Keine Benachrichtigung von Projections

Das alles passiert asynchron, über Polling – im nächsten Schritt.
