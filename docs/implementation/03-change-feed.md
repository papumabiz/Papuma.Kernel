# 03 – Change Feed: ChangeRecord und ChangeWriter

## Das Datenmodell: `ChangeRecord`

`ChangeRecord` ist das zentrale Objekt des Systems. Es repräsentiert einen einzelnen Eintrag im Change Feed – also einen aufgezeichneten Zustandsübergang.

```csharp
// src/Kernel/ChangeFeed/ChangeRecord.cs
public record ChangeRecord(
    long            SequenceId,
    string          Entity,
    string          EntityId,
    string          EventType,
    int             Version,
    string?         CorrelationId,
    string?         CausationId,
    string?         ActorId,
    string          PayloadJson,
    DateTimeOffset  Timestamp
);
```

`DateTimeOffset` passt besser zu `TIMESTAMPTZ` als `DateTime`, weil Offset/UTC-Semantik explizit bleibt.

**Warum ein `record`?**
`record` ist in C# semantisch immutable und hat eingebaute value equality. Das passt perfekt zu einem Event, das niemals verändert werden soll.

**Warum `PayloadJson` als `string` und nicht als generisches `T`?**
Weil der Kernel nicht wissen muss, was im Payload steckt. Das ist Aufgabe der Projection. Der Kernel speichert und transportiert – nicht mehr. Typisierung passiert bei der Interpretation, nicht im Speichermechanismus.

**Warum `CorrelationId`?**
Verknüpft alle Events, die zu **einem übergeordneten Vorgang** gehören. Typischerweise die Request-ID oder eine Prozess-ID. Wenn z.B. eine fachliche Operation sowohl einen User als auch ein Asset ändert, erhalten beide Change-Feed-Einträge dieselbe `CorrelationId`.

**Warum `CausationId`?**
Referenziert das Event, das dieses Event **direkt ausgelöst** hat. Bildet eine Kausalkette: Wenn eine Projection aus Event A ein Folge-Event B erzeugt, ist `CausationId` von B die `SequenceId` von A. Ermöglicht die Rekonstruktion von "Warum existiert dieses Event?" (siehe [02-datenbank.md](02-datenbank.md#correlation-vs-causation)).

**Warum `ActorId`?**
Identifiziert den Akteur, der die Änderung ausgelöst hat. Das ist kein fachlicher Payload-Inhalt, sondern ein Metadatum auf derselben Ebene wie `Timestamp`. Ermöglicht systemweite Queries wie "Zeige alle Änderungen von User X" oder "Zeige alle System-generierten Events" – ohne Payload-Parsing. Typische Werte: `"user:550e8400-..."`, `"system"`, `"migration"`, `"admin:..."`.

---

## Der `ChangeWriter`

Der `ChangeWriter` ist für das atomare Schreiben zuständig: CRUD-Mutation und Change Feed Append passieren in **einer** Datenbanktransaktion. Entweder beides, oder nichts.

```csharp
// src/Kernel/ChangeFeed/ChangeWriter.cs
public class ChangeWriter
{
    public async Task AppendAsync(
        NpgsqlTransaction transaction,
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        string? correlationId = null,
        string? causationId = null,
        string? actorId = null,
        CancellationToken ct = default)
    {
        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO change_feed
                (entity, entity_id, event_type, version, correlation_id, causation_id, actor_id, payload)
            VALUES
                (@entity, @entityId, @eventType, @version, @correlationId, @causationId, @actorId, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("entity",        entity);
        cmd.Parameters.AddWithValue("entityId",      entityId);
        cmd.Parameters.AddWithValue("eventType",     eventType);
        cmd.Parameters.AddWithValue("version",       version);
        cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("causationId",   (object?)causationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actorId",       (object?)actorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("payload",       payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

`ChangeWriter` ist hier bewusst transaktionsbasiert und zustandslos. Er bekommt die Transaktion vom aufrufenden Use-Case und verwendet keine eigene Connection.

## Optionale Erweiterung: Unit of Work

Die aktuelle Variante ist bereits eine explizite Unit of Work auf Use-Case-Ebene: der Use Case oeffnet die Transaktion, fuehrt mehrere Writes aus und committet einmal.

Wenn sich dieses Muster oft wiederholt, kann eine schlanke Unit of Work Abstraktion sinnvoll werden.

Sinnvoll, wenn:

- viele Handler identischen Connection/Transaction Boilerplate haben
- einheitliche Retry, Logging oder Telemetrie fuer Writes gewuenscht ist
- mehrere Repositories konsistent in einer fachlichen Operation koordiniert werden

```csharp
// src/Kernel/Transactions/IUnitOfWork.cs
public interface IUnitOfWork
{
    Task ExecuteAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action,
        CancellationToken ct = default);
}

// src/Kernel/Transactions/NpgsqlUnitOfWork.cs
public class NpgsqlUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlUnitOfWork(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task ExecuteAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await action(conn, tx, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
```

Nutzung im Use Case:

```csharp
await _uow.ExecuteAsync(async (conn, tx, ct) =>
{
    // CRUD Write
    // ChangeWriter.AppendAsync(...)
    // optional BusinessEventWriter + OutboxWriter
}, ct);
```

Wichtig: Auch mit Unit of Work bleibt die Regel gleich: eine fachliche Operation entspricht genau einer Datenbanktransaktion.

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
            transaction:   tx,
            entity:        "User",
            entityId:      userId.ToString(),
            eventType:     "UserEmailUpdated",
            version:       1,
            payloadJson:   payload,
            correlationId: null,   // optional: z.B. Request-ID für Tracing
            causationId:   null,   // optional: sequence_id des auslösenden Events
            actorId:       null,   // optional: z.B. "user:550e8400-..." oder "system"
            ct:            ct
        );

        // 3. Commit – atomarer Abschluss
        await tx.CommitAsync(ct);
    }
}
```

**Das ist der entscheidende Punkt:** Schritt 1 und 2 sind atomar. Es gibt keinen Zustand, in dem die E-Mail aktualisiert wurde, aber kein Feed-Eintrag existiert – und umgekehrt.

Zusatz: Das Write-Modell ist atomar, das Read-Modell ist asynchron. Daher gilt später für Projections at-least-once (siehe Kapitel 4).

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

## Business Events (ohne CRUD-Change)

Nicht jedes Event ist ein Change Event. Beispiel: `UserLoggedIn`.

Das Event kann fachlich wichtig sein (Audit, Analytics, Fraud Detection), ohne dass dafuer eine Domain-Tabelle geaendert werden muss.

```csharp
// src/Kernel/Events/BusinessEventWriter.cs
public class BusinessEventWriter
{
    public async Task<Guid> AppendAsync(
        NpgsqlTransaction transaction,
        string eventType,
        string? aggregateId,
        string payloadJson,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken ct = default)
    {
        var eventId = Guid.NewGuid();

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO business_event_log
                (event_id, event_type, aggregate_id, correlation_id, causation_id, payload)
            VALUES
                (@eventId, @eventType, @aggregateId, @correlationId, @causationId, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("aggregateId", (object?)aggregateId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("causationId", (object?)causationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
        return eventId;
    }
}
```

## Outbox fuer externe Verarbeitung

Wenn Business Events nach extern veroeffentlicht werden sollen (Message Broker, Webhook), schreibe sie in derselben Transaktion in `event_outbox`.

```csharp
// src/Kernel/Events/OutboxWriter.cs
public class OutboxWriter
{
    public async Task EnqueueAsync(
        NpgsqlTransaction transaction,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default)
    {
        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO event_outbox (event_id, event_type, payload)
            VALUES (@eventId, @eventType, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

## Beispiel: Login-Event ohne Domain-Mutation

```csharp
public async Task HandleLoginAsync(Guid userId, string ip, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    var payload = JsonSerializer.Serialize(new
    {
        UserId = userId,
        Ip = ip,
        LoggedInAt = DateTimeOffset.UtcNow
    });

    var eventId = await _businessEventWriter.AppendAsync(
        transaction: tx,
        eventType: "UserLoggedIn",
        aggregateId: userId.ToString(),
        payloadJson: payload,
        ct: ct);

    await _outboxWriter.EnqueueAsync(
        transaction: tx,
        eventId: eventId,
        eventType: "UserLoggedIn",
        payloadJson: payload,
        ct: ct);

    await tx.CommitAsync(ct);
}
```

Damit bleibt die Erzeugung des Events robust, auch wenn der externe Publisher gerade nicht verfuegbar ist.
