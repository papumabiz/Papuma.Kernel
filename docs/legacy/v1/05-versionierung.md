# 05 – Versionierung ohne Upcasting

## Das Problem mit klassischem Upcasting

Klassisches Event Sourcing löst Schema-Evolution mit einer **Upcaster-Pipeline**: Alte Events werden beim Lesen automatisch in die neueste Version transformiert.

Das klingt gut, hat aber folgende Probleme:
- Eine globale, zentrale Transformations-Kette, die schwer zu debuggen ist
- Alle Consumers müssen dieselbe "neueste Version" sehen
- Schemaänderungen betreffen das gesamte System gleichzeitig
- Schwer testbar, weil die Transformation implizit passiert

**Dieser Ansatz wird hier bewusst nicht verwendet.**

## Die Alternative: Projections als Versions-Interpreter

Der Kerngedanke:

> Events bleiben unveränderlich wie sie waren. Die Interpretation wandert in die Projections.

Jede Projection entscheidet selbst, wie sie verschiedene Versionen eines Events versteht. Das ist lokal, explizit und kontrollierbar.

---

## Versionierung in der Praxis

### Beispiel: `UserEmailUpdated` entwickelt sich

**Version 1** – E-Mail als einfacher String:
```json
{ "Email": "alice@example.com" }
```

**Version 2** – E-Mail als Objekt mit Verifikationsstatus:
```json
{ "Email": { "Value": "alice@example.com", "Verified": true } }
```

Die alten Version-1-Events bleiben **unverändert** in der Datenbank. Neue Events werden mit Version 2 gespeichert.

### Payload-Typen definieren

```csharp
// src/App/Features/Users/Events/UserEmailUpdatedV1.cs
public record UserEmailUpdatedV1(string Email);

// src/App/Features/Users/Events/UserEmailUpdatedV2.cs
public record UserEmailUpdatedV2(string Value, bool Verified);
```

### In der Projection: versions-bewusstes Deserialisieren

```csharp
private string ExtractEmail(ChangeRecord record) =>
    record.Version switch
    {
        1 => JsonSerializer.Deserialize<UserEmailUpdatedV1>(record.PayloadJson)!.Email,
        2 => JsonSerializer.Deserialize<UserEmailUpdatedV2>(record.PayloadJson)!.Value,
        _ => throw new InvalidOperationException(
                $"Unsupported version {record.Version} for {record.EventType}")
    };
```

Das ist der vollständige "Upcasting-Ersatz" – 4 Zeilen, lokal in der Projection, sofort debugbar.

---

## Versionierter Handler: Typisierung ohne Boilerplate

Wenn viele Projections denselben Event-Typ handhaben, lohnt sich ein Interface für typisierte Handler:

```csharp
// src/Kernel/Projections/IVersionedHandler.cs
public interface IVersionedHandler<T>
{
    int Version { get; }
    Task HandleAsync(T data, ChangeRecord record, CancellationToken ct = default);
}
```

### Implementierung für V1

```csharp
// src/App/Features/Users/Projections/Handlers/UserEmailUpdatedV1Handler.cs
public class UserEmailUpdatedV1Handler : IVersionedHandler<UserEmailUpdatedV1>
{
    public int Version => 1;

    public async Task HandleAsync(
        UserEmailUpdatedV1 data,
        ChangeRecord record,
        CancellationToken ct = default)
    {
        // data.Email ist stark typisiert – kein manuelles JSON-Parsen
        Console.WriteLine($"User {record.EntityId} email → {data.Email}");
        await Task.CompletedTask;
    }
}
```

### Implementierung für V2

```csharp
// src/App/Features/Users/Projections/Handlers/UserEmailUpdatedV2Handler.cs
public class UserEmailUpdatedV2Handler : IVersionedHandler<UserEmailUpdatedV2>
{
    public int Version => 2;

    public async Task HandleAsync(
        UserEmailUpdatedV2 data,
        ChangeRecord record,
        CancellationToken ct = default)
    {
        Console.WriteLine($"User {record.EntityId} email → {data.Value} (verified: {data.Verified})");
        await Task.CompletedTask;
    }
}
```

---

## Projection Registry: Automatisches Routing

Eine einfache Registry dispatcht anhand von `EventType` und `Version` an den richtigen Handler:

```csharp
// src/Kernel/Projections/ProjectionRegistry.cs
public class ProjectionRegistry
{
    // Key: (eventType, version)
    private readonly Dictionary<(string, int), Func<ChangeRecord, CancellationToken, Task>> _handlers = new();

    public void Register<TPayload>(
        string eventType,
        IVersionedHandler<TPayload> handler)
    {
        var key = (eventType, handler.Version);
        _handlers[key] = async (record, ct) =>
        {
            var data = JsonSerializer.Deserialize<TPayload>(record.PayloadJson)
                       ?? throw new InvalidOperationException("Deserialization returned null.");
            await handler.HandleAsync(data, record, ct);
        };
    }

    public async Task DispatchAsync(ChangeRecord record, CancellationToken ct = default)
    {
        var key = (record.EventType, record.Version);
        if (_handlers.TryGetValue(key, out var handle))
        {
            await handle(record, ct);
            return;
        }

        // Empfehlung: unbekannte Kombinationen protokollieren und in projection_failures aufnehmen,
        // statt sie still zu ignorieren.
        throw new InvalidOperationException(
            $"No handler registered for event '{record.EventType}' version {record.Version}.");
    }
}
```

### Nutzung in einer Projection

```csharp
// src/App/Features/Users/Projections/UserProjection.cs
public class UserProjection : IProjectionHandler
{
    public string Name => "user_read_model";
    public IReadOnlyCollection<string> EventTypes => new[] { "UserEmailUpdated" };

    private readonly ProjectionRegistry _registry;

    public UserProjection()
    {
        _registry = new ProjectionRegistry();
        _registry.Register("UserEmailUpdated", new UserEmailUpdatedV1Handler());
        _registry.Register("UserEmailUpdated", new UserEmailUpdatedV2Handler());
        // weitere Handler...
    }

    public Task HandleAsync(
        ChangeRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default)
        => _registry.DispatchAsync(record, ct);
}
```

---

## Wann braucht man wirklich Upcasting?

Es gibt genau zwei Fälle, in denen eine zentrale Upcasting-Pipeline sinnvoll wäre:

1. **Mehr als 5–6 aktive Versionen** desselben Events – dann wird der `switch`-Block unübersichtlich
2. **Viele unabhängige Consumers** (z.B. Kafka-Ecosystem) – dann hilft eine Schema Registry

In allen anderen Fällen: Projection-seitige Version-Interpretation ist die bessere Lösung.

## Betriebsregel für Versionierung

Neue Event-Versionen dürfen erst in Producer-Code gehen, wenn mindestens eine Projection-Version
den neuen Event-Typ versteht. Sonst laufen Worker in Fehlerzustände.

---

## Zusammenfassung: Was sich ändert, wenn ein Event evolviert

| Schritt | Was zu tun ist |
|---|---|
| Neues Payload-Schema entworfen | Neuen Record-Typ anlegen (`V2`) |
| `version` beim Schreiben erhöht | Im ChangeWriter `version: 2` übergeben |
| Projection erweitert | `case 2:` im `switch` ergänzen oder neuen `IVersionedHandler` registrieren |
| Alte Events | Bleiben unverändert – werden weiterhin mit `case 1:` verarbeitet |
| Replay | Funktioniert automatisch – alle Versionen werden verstanden |

Das ist der vollständige Prozess. Kein zentrales Schema-Registry, kein globaler Migration-Run.
