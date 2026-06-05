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
    string          ActorId,
    string          PayloadJson,
    DateTimeOffset  Timestamp,
    ScopeType       Scope,
    string?         TenantId
);
```

Hinweis zum aktuellen Stand:

- `Scope` ist explizit (`Platform` oder `Tenant`).
- `TenantId` ist nur bei `Scope = Tenant` gesetzt.

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

### Validierungsregeln (Pflicht)

Der `ChangeWriter` validiert alle Eingaben **vor** dem Schreiben. Das verhindert korrupte Daten im Feed und schützt gegen Log-Injection, XSS in Admin-UIs und semantisch ungültige Events.

| Parameter | Regel | Begründung |
|---|---|---|
| `entity` | Regex: `^[A-Za-z][A-Za-z0-9_]{1,100}$` | Verhindert Sonderzeichen, Log-Injection |
| `entityId` | Nicht leer, max. 200 Zeichen | Verhindert leere IDs und überlange Strings |
| `eventType` | Regex: `^[A-Za-z][A-Za-z0-9_]{2,100}$` | Verhindert Sonderzeichen, Log-Injection, XSS |
| `actorId` | **Pflichtfeld**, nicht leer, max. 200 Zeichen | Audit-Trail darf keine Lücken haben |
| `payloadJson` | Max. 256 KB (konfigurierbar) | Verhindert DoS durch überdimensionierte Payloads |
| `version` | `>= 1` | Semantisch ungültige Versionen verhindern |
| `idempotencyKey` | Optional, max. 200 Zeichen | Verhindert doppelte Writes bei Retry derselben fachlichen Aktion |

> ⚠️ **`actorId` ist ein Pflichtfeld.** Jede Änderung im System muss einem Akteur zugeordnet werden können. Ohne `actorId` ist der Audit-Trail unvollständig und Forensik bei Sicherheitsvorfällen unmöglich. Für automatische Prozesse: `"system:scheduler"`, `"system:migration"`, etc.

> ℹ️ **`idempotencyKey` ist optional, aber für retrybare Write-Pfade empfohlen.** Derselbe Key verhindert doppelte Change-Feed-Einträge bei erneuten Requests nach transienten Fehlern. Das gleiche Muster ist auch für den Business Event Log sinnvoll, wenn dieselbe fachliche Aktion dort ebenfalls mehrfach geschrieben werden könnte.

```csharp
// src/Kernel/ChangeFeed/ChangeWriterOptions.cs
public class ChangeWriterOptions
{
    /// <summary>
    /// Maximale Payload-Größe in Bytes. Default: 256 KB.
    /// Für größere Daten: Referenz-Pattern verwenden (Payload enthält URL/ID, Daten in Blob-Storage).
    /// </summary>
    public int MaxPayloadSizeBytes { get; set; } = 256 * 1024;
}
```

Wenn der Write-Pfad retrybar ist, sollte zusätzlich ein `idempotencyKey` gesetzt werden. Dann kann der gleiche fachliche Vorgang ohne doppelte Einträge erneut ausgeführt werden.

### Guideline: Wo ist der Idempotency-Key Pflicht?

| Entry-Point | Change Feed (`change_feed`) | Event Log (`business_event_log`) | Empfehlung |
|---|---|---|---|
| HTTP Command Endpoint mit Client-Retry | Pflicht | Aktiv nutzen | Gleichen Key pro fachlichem Command durchreichen |
| Message Consumer mit mindestens einmaliger Zustellung | Pflicht | Aktiv nutzen | Message-ID oder deterministischen Command-Key verwenden |
| Geplanter Job mit Retry-Policy | Pflicht | Aktiv nutzen | Job-Run + fachlichen Schlüssel kombinieren |
| Interner synchroner Pfad ohne Retry und ohne externen Re-Dispatch | Empfohlen | Optional | Key kann gesetzt werden, ist aber nicht zwingend |
| Reine technische Status-/Diagnose-Events | Nicht relevant | Optional | Nur setzen, wenn Duplikate fachlich stoeren |

Kurzregel:
1. Change Feed immer idempotent-key-faehig behandeln.
2. Event Log nur fuer retrybare Pfade aktiv mit Key nutzen.
3. Der gleiche fachliche Vorgang muss in einem Retry denselben Key behalten.

```csharp
// src/Kernel/ChangeFeed/ChangeWriter.cs
public class ChangeWriter
{
    private readonly ChangeWriterOptions _options;

    public ChangeWriter(ChangeWriterOptions? options = null)
    {
        _options = options ?? new ChangeWriterOptions();
    }

    public async Task AppendAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        string actorId,
        string? correlationId = null,
        string? causationId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        // Validierung (Defense in Depth) – delegiert an shared InputValidator
        ValidateInputs(entity, entityId, eventType, version, payloadJson, actorId, idempotencyKey);

        await SetScopeOnConnectionAsync(transaction.Connection!, scope, ct);

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO change_feed
                (scope, tenant_id, entity, entity_id, event_type, version, correlation_id, causation_id, actor_id, payload, idempotency_key)
            VALUES
                (@scope, @tenantId, @entity, @entityId, @eventType, @version, @correlationId, @causationId, @actorId, @payload::jsonb, @idempotencyKey)
            """;

        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("entity",        entity);
        cmd.Parameters.AddWithValue("entityId",      entityId);
        cmd.Parameters.AddWithValue("eventType",     eventType);
        cmd.Parameters.AddWithValue("version",       version);
        cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("causationId",   (object?)causationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actorId",       actorId);
        cmd.Parameters.AddWithValue("payload",       payloadJson);
        cmd.Parameters.AddWithValue("idempotencyKey", (object?)idempotencyKey ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void ValidateInputs(
        string entity, string entityId, string eventType,
        int version, string payloadJson, string actorId)
    {
        // Alle Validierungen delegieren an den shared InputValidator.
        // Entity und EventType verwenden separate Regex-Patterns:
        //   Entity:    ^[A-Za-z][A-Za-z0-9_]{1,100}$
        //   EventType: ^[A-Za-z][A-Za-z0-9_]{2,100}$  (min. 3 Zeichen)
        // Alle Regex-Patterns sind compiled und time-bounded (100ms Timeout).
        InputValidator.ValidateEntity(entity);
        InputValidator.ValidateEntityId(entityId);
        InputValidator.ValidateEventType(eventType);
        InputValidator.ValidateVersion(version);
        InputValidator.ValidateActorId(actorId);
        // Payload-Größe wird in UTF-8 Bytes gemessen, nicht in Zeichenlänge.
        InputValidator.ValidatePayloadSize(payloadJson, _options.MaxPayloadSizeBytes);
    }

    private static async Task SetScopeOnConnectionAsync(
        NpgsqlConnection connection,
        ScopeContext scope,
        CancellationToken ct)
    {
        await using (var scopeCmd = connection.CreateCommand())
        {
            scopeCmd.CommandText = "SET LOCAL app.current_scope = @scope";
            scopeCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            await scopeCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var tenantCmd = connection.CreateCommand())
        {
            tenantCmd.CommandText = "SET LOCAL app.current_tenant = @tenantId";
            tenantCmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
            await tenantCmd.ExecuteNonQueryAsync(ct);
        }
    }
}
```

**Wichtige Änderungen gegenüber der Minimalversion:**

1. **`actorId` ist jetzt Pflichtparameter** (nicht mehr optional mit Default `null`). Er steht vor den optionalen Parametern.
2. **Eingabevalidierung** schützt gegen korrupte Daten, Log-Injection und überdimensionierte Payloads.
3. **Konfigurierbare Payload-Größe** über `ChangeWriterOptions`.

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

// src/Kernel/Transactions/UnitOfWorkOptions.cs
public class UnitOfWorkOptions
{
    /// <summary>
    /// Maximale Anzahl Retries bei transienten Datenbankfehlern (Deadlock, Serialization Failure).
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Basis-Delay zwischen Retries. Jitter wird automatisch hinzugefügt.
    /// </summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}

// src/Kernel/Transactions/NpgsqlUnitOfWork.cs
public class NpgsqlUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly UnitOfWorkOptions _options;
    private readonly ILogger<NpgsqlUnitOfWork>? _logger;

    // PostgreSQL-Fehlercodes, die transient sind und einen Retry rechtfertigen
    private static readonly HashSet<string> TransientSqlStates = new()
    {
        "40001", // serialization_failure
        "40P01", // deadlock_detected
        "08006", // connection_failure
        "08001", // sqlclient_unable_to_establish_sqlconnection
        "57P03", // cannot_connect_now
    };

    public NpgsqlUnitOfWork(
        NpgsqlDataSource dataSource,
        UnitOfWorkOptions? options = null,
        ILogger<NpgsqlUnitOfWork>? logger = null)
    {
        _dataSource = dataSource;
        _options = options ?? new UnitOfWorkOptions();
        _logger = logger;
    }

    public async Task ExecuteAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                await action(conn, tx, ct);
                await tx.CommitAsync(ct);
                return; // Erfolg
            }
            catch (NpgsqlException ex) when (
                attempt < _options.MaxRetries &&
                ex.SqlState is not null &&
                TransientSqlStates.Contains(ex.SqlState))
            {
                await tx.RollbackAsync(ct);
                var delay = _options.BaseRetryDelay * Math.Pow(2, attempt - 1);
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 50));
                _logger?.LogWarning(
                    "Transient DB error (SqlState={SqlState}, attempt {Attempt}/{Max}), retrying in {Delay}ms.",
                    ex.SqlState, attempt, _options.MaxRetries, (delay + jitter).TotalMilliseconds);
                await Task.Delay(delay + jitter, ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }
}
```

> **Warum Retry im Unit of Work?** Transiente Datenbankfehler (Deadlocks, Serialization Failures, kurzzeitige Connection-Probleme) sind in Produktion unter Last **normal**. Ohne Retry schlagen diese Operationen sofort fehl und der Fehler propagiert zum Client. Mit Retry werden die meisten transienten Fehler transparent aufgelöst. Jitter verhindert Thundering-Herd-Effekte bei gleichzeitigen Retries.

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

    public async Task HandleAsync(
        Guid userId,
        string newEmail,
        string actorId,              // Pflicht: wer löst die Änderung aus?
        string? correlationId = null, // optional: z.B. Request-ID für Tracing
        CancellationToken ct = default)
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
            scope:         ScopeContext.Tenant("acme"),
            entity:        "User",
            entityId:      userId.ToString(),
            eventType:     "UserEmailUpdated",
            version:       1,
            payloadJson:   payload,
            actorId:       actorId,        // Pflicht: z.B. "user:550e8400-..." oder "admin:..."
            correlationId: correlationId,   // optional: Request-ID für Tracing
            ct:            ct
        );

        // 3. Commit – atomarer Abschluss
        await tx.CommitAsync(ct);
    }
}
```

> **Beachte:** `actorId` wird vom Use-Case-Handler als Pflichtparameter entgegengenommen. In der Praxis kommt dieser Wert aus dem Authentication-Context (z.B. `HttpContext.User`). Eine Middleware oder Pipeline kann den `actorId` automatisch aus dem JWT-Token extrahieren und an den Handler übergeben.

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
        ScopeContext scope,
        string eventType,
        string actorId,
        string payloadJson,
        string? entity = null,
        string? entityId = null,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken ct = default)
    {
        var eventId = Guid.NewGuid();

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO business_event_log
                (event_id, scope, tenant_id, event_type, entity, entity_id, actor_id,
                 correlation_id, causation_id, payload)
            VALUES
                (@eventId, @scope, @tenantId, @eventType, @entity, @entityId, @actorId,
                 @correlationId, @causationId, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("entity", (object?)entity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("entityId", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actorId", actorId);
        cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("causationId", (object?)causationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
        return eventId;
    }
}
```

**Änderungen gegenüber der Minimalversion:**
- `aggregate_id` → `entity` + `entity_id` (konsistent mit `change_feed`)
- `actorId` ist Pflichtparameter

## Outbox fuer externe Verarbeitung

Wenn Business Events nach extern veroeffentlicht werden sollen (Message Broker, Webhook), schreibe sie in derselben Transaktion in `event_outbox`.

```csharp
// src/Kernel/Events/OutboxWriter.cs
public class OutboxWriter
{
    public async Task EnqueueAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default)
    {
        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO event_outbox (scope, tenant_id, event_id, event_type, payload)
            VALUES (@scope, @tenantId, @eventId, @eventType, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

## Beispiel: Login-Event ohne Domain-Mutation

```csharp
public async Task HandleLoginAsync(Guid userId, string ip, string actorId, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    var payload = JsonSerializer.Serialize(new
    {
        Ip = ip,
        LoggedInAt = DateTimeOffset.UtcNow
    });

    var eventId = await _businessEventWriter.AppendAsync(
        transaction: tx,
        scope: ScopeContext.Tenant("acme"),
        eventType: "UserLoggedIn",
        actorId: actorId,              // Pflicht: wer loggt sich ein?
        entity: "User",                // konsistent mit change_feed
        entityId: userId.ToString(),
        payloadJson: payload,
        ct: ct);

    await _outboxWriter.EnqueueAsync(
        transaction: tx,
        scope: ScopeContext.Tenant("acme"),
        eventId: eventId,
        eventType: "UserLoggedIn",
        payloadJson: payload,
        ct: ct);

    await tx.CommitAsync(ct);
}
```

> **Beachte:** `UserId` ist nicht mehr im Payload, weil es bereits als `entityId` im Event-Metadatum steht. Payload enthält nur die **zusätzlichen** Informationen (IP, Zeitpunkt). Das vermeidet Redundanz und hält den Payload minimal.

Damit bleibt die Erzeugung des Events robust, auch wenn der externe Publisher gerade nicht verfuegbar ist.
