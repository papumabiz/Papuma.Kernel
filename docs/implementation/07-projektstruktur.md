# 07 – Projektstruktur, Konventionen und Phasenplan

## Verzeichnisstruktur

```
src/
├── Kernel/                         ← wiederverwendbare Bausteine (kein Business-Code)
│   ├── ChangeFeed/
│   │   ├── ChangeRecord.cs
│   │   └── ChangeWriter.cs
│   ├── Projections/
│   │   ├── IProjectionHandler.cs
│   │   ├── IVersionedHandler.cs
│   │   ├── ProjectionWorker.cs
│   │   ├── ProjectionRegistry.cs
│   │   ├── ProjectionExtensions.cs
│   │   └── ReplayService.cs
│   └── Gdpr/
│       └── GdprProcessor.cs
│
├── App/                            ← Anwendungslogik, Features
│   ├── Features/
│   │   ├── Users/
│   │   │   ├── Events/
│   │   │   │   ├── UserEmailUpdatedV1.cs
│   │   │   │   └── UserEmailUpdatedV2.cs
│   │   │   ├── Projections/
│   │   │   │   ├── UserReadModelProjection.cs
│   │   │   │   └── Handlers/
│   │   │   │       ├── UserEmailUpdatedV1Handler.cs
│   │   │   │       └── UserEmailUpdatedV2Handler.cs
│   │   │   ├── UpdateUserEmailHandler.cs
│   │   │   └── UserDeletionService.cs
│   │   └── Assets/
│   │       ├── Events/
│   │       └── Projections/
│   └── Program.cs
│
└── Infrastructure/
    └── Postgres/
        └── schema.sql              ← Init-Skript (aus 02-datenbank.md)
```

### Die wichtigste Konvention

> **Features besitzen ihre Projections.**

Nicht:
```
Kernel/Projections/UserProjection.cs  ← falsch
```

Sondern:
```
App/Features/Users/Projections/UserReadModelProjection.cs  ← richtig
```

**Warum?** Sobald der Kernel Feature-spezifische Projections enthält, wird er zum "Marten 2" – einem Framework, das zu viel weiß. Der Kernel stellt nur die Infrastruktur bereit. Features entscheiden, wie sie den Feed interpretieren.

---

## Phasenplan

### Phase 1 – Foundation (baue das zuerst)

**Ziel:** Ein echtes Ende-zu-Ende-System, minimal aber funktional.

**Scope:**

1. Datenbank-Schema anlegen (aus [02-datenbank.md](02-datenbank.md))
2. `ChangeRecord` implementieren
3. `ChangeWriter` implementieren
4. `UpdateUserEmail` als erste echte Mutation implementieren (CRUD + Feed in einer Transaktion)
5. `ProjectionWorker` implementieren
6. `UserReadModelProjection` – erste echte Projection
7. Replay manuell testen: Checkpoint auf 0 zurücksetzen, Worker läuft durch
8. Idempotenz für Projection-Write-Paths sicherstellen (`ON CONFLICT DO UPDATE`)
9. `projection_failures` Tabelle anlegen (ohne Dead-Letter-Überspringen im ersten Schritt)

**Noch nicht:**
- Keine generischen Abstraktionen
- Kein NuGet-Package
- Kein versionierter Payload (Version = 1 reicht)
- Keine DSGVO

**Warum so minimal?**  
Du brauchst ein echtes Problem, bevor du abstrahierst. Wenn du eine Projection gebaut hast, verstehst du, was der Kernel leisten muss. Vorher ist es Spekulation.

---

### Phase 2 – Reale Projections

**Ziel:** 3 echte, produktionsnahe Projections bauen.

Beispiele:
- `UserSearchProjection` → schreibt in Suchindex (z.B. Manticore, Elasticsearch)
- `ActivityFeedProjection` → baut einen Activity-Stream
- `NotificationProjection` → sendet Push-Notifications

Ergaenzung in Phase 2:

- `business_event_log` fuer fachliche Ereignisse ohne zwingende Mutation
- `event_outbox` fuer robuste externe Zustellung (Broker/Webhook)
- Publisher-Worker mit Retry und idempotenter Zustellung

**Wichtig:** Erst nach 3 echten Projections abstrahieren. Nicht vorher. Sonst abstrahiert man Fantasie.

---

### Phase 3 – Versionierung und Replay-Tooling

Erst jetzt:
- `version` in Payloads nutzen
- `IVersionedHandler<T>` einführen
- `ProjectionRegistry` für Dispatch
- `ReplayService` fertig stellen
- CLI-Tool oder Admin-Endpoint für Replay: `replay --projection user_read_model`

---

### Phase 4 – Kernelisierung

Erst wenn sich Patterns stabil wiederholen:
- Base Classes/Helpers extrahieren
- Conventions dokumentieren
- Optional: als NuGet-Package herauslösen (`Papuma.Kernel`)

**Die häufigste Falle:** Viele Frameworks sterben daran, dass zuerst abstrahiert wird und dann reale Probleme gesucht werden. Dieser Phasenplan dreht das bewusst um.

---

## Schritt-für-Schritt-Einstieg (konkret)

### Schritt 1: Schema anlegen

```bash
psql -U postgres -d mydb -f src/Infrastructure/Postgres/schema.sql
```

### Schritt 2: Verbindung konfigurieren

```csharp
// Program.cs
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Connection string 'Postgres' not configured.");

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddSingleton<ChangeWriter>();
// GdprProcessor erst ab Phase 3/4 registrieren
```

```json
// appsettings.json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=mydb;Username=postgres;Password=secret"
  }
}
```

### Schritt 3: Erste Mutation implementieren

Implementiere `UpdateUserEmail` wie in [03-change-feed.md](03-change-feed.md) beschrieben. Teste, dass beide Schreibvorgänge (CRUD + Feed) in einer Transaktion passieren.

### Schritt 4: Erste Projection implementieren

Implementiere `UserReadModelProjection` wie in [04-projections.md](04-projections.md). Registriere den Worker. Überprüfe, dass der Checkpoint in `projection_checkpoint` geschrieben wird.

### Schritt 5: Replay testen

Setze den Checkpoint manuell auf 0:

```sql
UPDATE projection_checkpoint SET last_sequence_id = 0 WHERE projection_name = 'user_read_model';
```

Starte den Worker. Er sollte alle Events neu verarbeiten – und am Ende zum identischen Read-Model-Zustand kommen.

---

## NuGet-Pakete

```xml
<!-- src/Kernel/Kernel.csproj -->
<ItemGroup>
    <PackageReference Include="Npgsql" Version="10.*" />
    <PackageReference Include="Npgsql.DependencyInjection" Version="10.*" />
    <PackageReference Include="Dapper" Version="2.*" />          <!-- optional, für Read-Queries -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
</ItemGroup>
```

`System.Text.Json` ist im .NET 10 SDK bereits enthalten.

---

## Optionale spätere Erweiterungen

Diese Dinge werden **nicht** in Phase 1–2 gebaut, können aber später ergänzt werden:

| Feature | Wann sinnvoll |
|---|---|
| `LISTEN/NOTIFY` (Postgres Push) | Wenn Polling-Latenz >200ms zu viel wird |
| Projection Leasing | Bei horizontaler Skalierung (mehrere App-Instanzen) |
| Snapshotting | Bei sehr langen Streams (>100k Events pro Entity) |
| DotNetCore.CAP / Wolverine | Als Dispatcher, wenn externe Services benötigt werden |

## Betriebsinvarianten (nicht optional)

Diese Regeln gelten ab dem ersten produktiven Einsatz:

1. Projection-Handler sind idempotent.
2. At-least-once Semantik ist akzeptiert und getestet.
3. Für Poison Events existiert ein operativer Pfad (`projection_failures` Monitoring + Manual/Auto Skip).
4. Replay wird in Staging regelmäßig geprobt.
5. Neue Event-Versionen werden erst nach Consumer-Readiness ausgerollt.

---

## Zusammenfassung: Was dieses System ist

| Aspekt | Beschreibung |
|---|---|
| **Storage** | PostgreSQL JSONB – kein Event Store, kein Marten |
| **Write** | CRUD + Change Feed Append, atomar in einer Transaktion |
| **Read** | Projections bauen Read Models asynchron auf |
| **Replay** | Checkpoint zurücksetzen, Worker läuft durch |
| **Migration** | Nur in Projections, nicht in Events |
| **DSGVO** | Explizite Redaktion, transparent und testbar |
| **Skalierung** | Jede Projection ein eigener Worker, beliebig viele parallel |

> Writes commit facts. Projections converge asynchronously.
