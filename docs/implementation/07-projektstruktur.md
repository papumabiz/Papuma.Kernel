# 07 – Projektstruktur, Konventionen und Phasenplan

## Library-first: `Papuma.Kernel` als eigenständiges NuGet-Package

Der Kernel wird **von Anfang an** als eigenständige Library entwickelt – nicht erst in Phase 4. Das ist eine bewusste Entscheidung:

| Argument | Begründung |
|---|---|
| **Klare Dependency-Richtung** | Die App referenziert den Kernel, nie umgekehrt. Das erzwingt saubere Schnittstellen. |
| **Unabhängige Testbarkeit** | Der Kernel wird isoliert getestet, ohne App-Abhängigkeiten. |
| **Wiederverwendbarkeit** | Wenn ein zweites Projekt den Kernel nutzen will, ist er sofort verfügbar. |
| **Disziplin** | Verhindert, dass Feature-Code in den Kernel wandert. Die Package-Grenze ist eine harte Barriere. |
| **Versionierung** | Kernel-Releases können unabhängig von der App versioniert werden. |

> **Regel:** Der Kernel kennt keine Features, keine Entities, keine Business-Logik. Er stellt nur Infrastruktur bereit: Change Feed, Projections, Tenancy, GDPR.

### Solution-Struktur

```
Papuma.Kernel.slnx
│
├── src/
│   ├── Papuma.Kernel/              ← NuGet-Package (Infrastruktur-Kern)
│   │   ├── Papuma.Kernel.csproj
│   │   ├── PapumaKernelOptions.cs
│   │   ├── ServiceCollectionExtensions.cs
│   │   ├── ChangeFeed/
│   │   │   ├── ChangeRecord.cs
│   │   │   ├── ChangeWriter.cs
│   │   │   └── ChangeWriterOptions.cs
│   │   ├── Events/
│   │   │   ├── BusinessEventWriter.cs
│   │   │   ├── BusinessEventWriterOptions.cs
│   │   │   ├── IOutboxPublisher.cs
│   │   │   ├── OutboxWriter.cs
│   │   │   └── OutboxWriterOptions.cs
│   │   ├── Transactions/
│   │   │   ├── IUnitOfWork.cs
│   │   │   ├── NpgsqlUnitOfWork.cs
│   │   │   └── UnitOfWorkOptions.cs
│   │   ├── Projections/
│   │   │   ├── IProjectionHandler.cs
│   │   │   ├── IExternalProjectionHandler.cs
│   │   │   ├── IReplayableProjection.cs
│   │   │   ├── IVersionedHandler.cs
│   │   │   ├── ProjectionWorker.cs
│   │   │   ├── ProjectionWorkerOptions.cs
│   │   │   ├── ProjectionRegistry.cs
│   │   │   ├── ProjectionExtensions.cs
│   │   │   └── ReplayService.cs
│   │   ├── Tenancy/
│   │   │   ├── TenantContext.cs
│   │   │   ├── ITenantDataSourceFactory.cs
│   │   │   └── TenantDataSourceFactory.cs
│   │   ├── Validation/
│   │   │   └── InputValidator.cs
│   │   ├── Gdpr/
│   │   │   ├── GdprProcessor.cs
│   │   │   ├── EntityHistory.cs
│   │   │   ├── BusinessEventRecord.cs
│   │   │   └── RedactionResult.cs
│   │   └── Schema/
│   │       └── schema.sql          ← Init-Skript (als Embedded Resource)
│   │
│   └── Papuma.Kernel.AspNetCore/   ← HTTP-Integration (separates Package)
│       ├── Papuma.Kernel.AspNetCore.csproj
│       └── Tenancy/
│           ├── ITenantResolver.cs
│           ├── TenantMiddleware.cs
│           └── TenantMiddlewareExtensions.cs
│
├── tests/
│   ├── Papuma.Kernel.Tests/        ← Unit- und Integrationstests für den Kernel
│   │   └── Papuma.Kernel.Tests.csproj
│   └── Papuma.Kernel.AspNetCore.Tests/  ← Tests für das AspNetCore-Package
│       └── Papuma.Kernel.AspNetCore.Tests.csproj
│
└── docs/
    └── implementation/             ← diese Dokumentation
```

> **Hinweis:** `ITenantResolver` und `TenantMiddleware` leben in `Papuma.Kernel.AspNetCore`, weil sie von `Microsoft.AspNetCore.Http` abhängen. Der Kern-Package `Papuma.Kernel` hat keine ASP.NET Core-Abhängigkeit und kann auch in Konsolen-Apps oder Worker-Services verwendet werden.

### Wie die konsumierende App aussieht (Beispiel)

Die App ist ein **separates Repository/Projekt**, das `Papuma.Kernel` als NuGet-Package oder Projekt-Referenz einbindet:

```
MyApp.slnx
│
├── src/
│   └── MyApp/
│       ├── MyApp.csproj            ← referenziert Papuma.Kernel
│       ├── Features/
│       │   ├── Users/
│       │   │   ├── Events/
│       │   │   │   ├── UserEmailUpdatedV1.cs
│       │   │   │   └── UserEmailUpdatedV2.cs
│       │   │   ├── Projections/
│       │   │   │   ├── UserReadModelProjection.cs
│       │   │   │   └── Handlers/
│       │   │   │       ├── UserEmailUpdatedV1Handler.cs
│       │   │   │       └── UserEmailUpdatedV2Handler.cs
│       │   │   ├── UpdateUserEmailHandler.cs
│       │   │   └── UserDeletionService.cs
│       │   └── Assets/
│       │       ├── Events/
│       │       └── Projections/
│       ├── Tenancy/
│       │   ├── HeaderTenantResolver.cs
│       │   └── JwtClaimTenantResolver.cs
│       ├── HealthChecks/
│       │   └── ProjectionHealthCheck.cs
│       └── Program.cs
│
└── tests/
    └── MyApp.Tests/
```

```xml
<!-- MyApp.csproj -->
<ItemGroup>
    <!-- Option 1: NuGet-Package (empfohlen für Produktion) -->
    <PackageReference Include="Papuma.Kernel" Version="0.1.0" />

    <!-- Option 2: Projekt-Referenz (während der Entwicklung) -->
    <!-- <ProjectReference Include="../../Papuma.Kernel/src/Papuma.Kernel/Papuma.Kernel.csproj" /> -->
</ItemGroup>
```

### Die wichtigste Konvention

> **Features besitzen ihre Projections. Der Kernel besitzt keine.**

Nicht:
```
Papuma.Kernel/Projections/UserProjection.cs  ← falsch (Feature-Code im Kernel)
```

Sondern:
```
MyApp/Features/Users/Projections/UserReadModelProjection.cs  ← richtig (Feature-Code in der App)
```

**Warum?** Sobald der Kernel Feature-spezifische Projections enthält, wird er zum "Marten 2" – einem Framework, das zu viel weiß. Der Kernel stellt nur die Infrastruktur bereit. Features entscheiden, wie sie den Feed interpretieren.

### Was gehört in den Kernel, was in die App?

| Kernel (`Papuma.Kernel`) | AspNetCore (`Papuma.Kernel.AspNetCore`) | App (`MyApp`) |
|---|---|---|
| `ChangeRecord`, `ChangeWriter` | | `UserEmailUpdatedV1`, `UserEmailUpdatedV2` |
| `ProjectionWorker`, `IProjectionHandler` | | `UserReadModelProjection` |
| `GdprProcessor` | | `UserDeletionService` |
| `TenantContext`, `ITenantDataSourceFactory` | `ITenantResolver`, `TenantMiddleware` | `JwtClaimTenantResolver`, `HeaderTenantResolver` |
| `IUnitOfWork`, `NpgsqlUnitOfWork` | | `UpdateUserEmailHandler` |
| `AddPapumaKernel()`, `AddProjection<T>()`, `AddReplayService()` | `AddPapumaTenancy<T>()`, `UseTenantResolution()` | `Program.cs` (Registrierung) |
| `schema.sql` (Kernel-Tabellen) | | Domain-Tabellen (`users`, `assets`, etc.) |

---

## Phasenplan

### Phase 1 – Foundation (baue das zuerst)

**Ziel:** Ein echtes Ende-zu-Ende-System, minimal aber funktional. **Inklusive Sicherheitsgrundlagen und Multi-Tenancy.**

**Scope:**

1. Datenbank-Schema anlegen mit `tenant_id` (aus [02-datenbank.md](02-datenbank.md) + [08-multi-tenancy.md](08-multi-tenancy.md))
2. Row-Level Security (RLS) für alle Tabellen aktivieren
3. `TenantContext`, `ITenantResolver`, `TenantMiddleware` implementieren
4. `ChangeRecord` implementieren
5. `ChangeWriter` implementieren **mit Eingabevalidierung** (Event-Type-Regex, Payload-Größenlimit, `actorId` als Pflichtfeld)
6. `ChangeWriterOptions` implementieren
7. `UpdateUserEmail` als erste echte Mutation implementieren (CRUD + Feed in einer Transaktion, mit `actorId` und `tenantId`)
8. `ProjectionWorker` implementieren (mit `xmin`-Sichtbarkeitsfilter)
9. `UserReadModelProjection` – erste echte Projection
10. Replay manuell testen: Checkpoint auf 0 zurücksetzen, Worker läuft durch
11. Idempotenz für Projection-Write-Paths sicherstellen (`ON CONFLICT DO UPDATE`)
12. `projection_failures` Tabelle anlegen (mit exponentiellem Backoff)
13. `GdprProcessor` implementieren (mit `actorId`, `reason`, Business-Event-Log-Redaktion)
14. `NpgsqlUnitOfWork` mit Retry-Logik für transiente Fehler
15. Health-Check-Endpoint für Projection-Lag

**Noch nicht:**
- Keine generischen Abstraktionen über das Dokumentierte hinaus
- Kein versionierter Payload (Version = 1 reicht)
- Kein Database-per-Tenant (Shared Database reicht)
- Keine Snapshots

> **Hinweis:** `Papuma.Kernel` wird von Anfang an als eigenständige Library entwickelt und kann ab Phase 1 als NuGet-Package oder Projekt-Referenz von der App konsumiert werden. Die "Kernelisierung" in Phase 4 bezieht sich auf das Extrahieren von **Patterns und Conventions**, nicht auf die Package-Struktur.

**Warum mehr als vorher in Phase 1?**
Sicherheitsgrundlagen (`actorId`, Validierung, RLS) und Multi-Tenancy nachträglich einzuführen ist extrem teuer. Diese Dinge müssen von Anfang an im Kern sein. Der Aufwand ist überschaubar, der Nutzen enorm.

---

### Phase 2 – Reale Projections

**Ziel:** 3 echte, produktionsnahe Projections bauen.

Beispiele:
- `UserSearchProjection` → schreibt in Suchindex (z.B. Manticore, Elasticsearch)
- `ActivityFeedProjection` → baut einen Activity-Stream
- `NotificationProjection` → sendet Push-Notifications

Ergänzungen in Phase 2:

- `business_event_log` für fachliche Ereignisse ohne zwingende Mutation
- `event_outbox` für robuste externe Zustellung (Broker/Webhook)
- Publisher-Worker mit Retry und idempotenter Zustellung
- Dead-Letter-Alerting (Log-Level Critical, Metrik)
- Admin-Endpoint für manuellen Retry von Dead-Letter-Events
- OpenTelemetry-Integration (Metriken, Tracing mit `correlation_id`)

**Wichtig:** Erst nach 3 echten Projections abstrahieren. Nicht vorher. Sonst abstrahiert man Fantasie.

---

### Phase 3 – Versionierung, Replay-Tooling und Enterprise-Features

Erst jetzt:
- `version` in Payloads nutzen
- `IVersionedHandler<T>` einführen
- `ProjectionRegistry` für Dispatch
- `ReplayService` fertig stellen
- CLI-Tool oder Admin-Endpoint für Replay: `replay --projection user_read_model`
- Snapshot-Strategie evaluieren und nur bei Bedarf einführen (Projection Snapshots)
- Database-per-Tenant als Alternative zu Shared Database (siehe [08-multi-tenancy.md](08-multi-tenancy.md))
- `ITenantDataSourceFactory` für Database-per-Tenant
- Retention-Policy für Change Feed und Business Event Log (Partitionierung)
- Idempotenz im `ChangeWriter` (Unique-Constraint auf `correlation_id`)

---

### Phase 4 – Stabilisierung und Erweiterung

Erst wenn sich Patterns stabil wiederholen:
- Base Classes/Helpers im Kernel extrahieren
- Conventions dokumentieren und als Teil des NuGet-Packages ausliefern
- Erstes stabiles Release (`1.0.0`) des NuGet-Packages
- Hybrid-Multi-Tenancy (Shared + Database-per-Tenant je nach Tier)
- Schema-Registry für Event-Typen (bei >10 Event-Typen)

**Die häufigste Falle:** Viele Frameworks sterben daran, dass zuerst abstrahiert wird und dann reale Probleme gesucht werden. Dieser Phasenplan dreht das bewusst um – die Package-Struktur steht von Anfang an, aber die **API-Stabilisierung** erfolgt erst nach realer Nutzung.

---

## Schritt-für-Schritt-Einstieg (konkret)

### Schritt 1: Schema anlegen

Das Init-Skript liegt im Kernel-Package unter `Schema/schema.sql`:

```bash
psql -U postgres -d mydb -f src/Papuma.Kernel/Schema/schema.sql
```

### Schritt 2: Verbindung und Tenancy konfigurieren

```csharp
// Program.cs
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Connection string 'Postgres' not configured.");

builder.Services.AddNpgsqlDataSource(connectionString);

// Registriert ChangeWriter, BusinessEventWriter, OutboxWriter, GdprProcessor, IUnitOfWork
builder.Services.AddPapumaKernel();

// Projections registrieren
builder.Services.AddProjection<UserReadModelProjection>();
builder.Services.AddReplayService();

// Tenant-Auflösung (aus Papuma.Kernel.AspNetCore)
builder.Services.AddPapumaTenancy<HeaderTenantResolver>();

var app = builder.Build();
app.UseTenantResolution();
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

### `Papuma.Kernel` (die Library)

```xml
<!-- src/Papuma.Kernel/Papuma.Kernel.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>Papuma.Kernel</PackageId>
    <Version>0.1.0</Version>
    <Authors>Papuma</Authors>
    <Description>Minimal event-informed architecture kernel: Change Feed, Projections, Multi-Tenancy, GDPR.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Npgsql" Version="10.*" />
    <PackageReference Include="Npgsql.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
  </ItemGroup>

  <!-- Schema als Embedded Resource ausliefern -->
  <ItemGroup>
    <EmbeddedResource Include="Schema/schema.sql" />
  </ItemGroup>
</Project>
```

### Konsumierende App

```xml
<!-- MyApp/MyApp.csproj -->
<ItemGroup>
    <PackageReference Include="Papuma.Kernel" Version="0.1.0" />
    <PackageReference Include="Dapper" Version="2.*" />          <!-- optional, für Read-Queries in der App -->
</ItemGroup>
```

> **Beachte:** `Dapper` ist eine App-Abhängigkeit, nicht eine Kernel-Abhängigkeit. Der Kernel verwendet nur `Npgsql` direkt. Die App kann Dapper für ihre Read-Queries verwenden, aber der Kernel erzwingt es nicht.

`System.Text.Json` ist im .NET 10 SDK bereits enthalten.

---

## Optionale spätere Erweiterungen

Diese Dinge werden **nicht** in Phase 1–2 gebaut, können aber später ergänzt werden:

| Feature | Wann sinnvoll |
|---|---|
| `LISTEN/NOTIFY` (Postgres Push) | Wenn Polling-Latenz >200ms zu viel wird |
| Projection Leasing / Advisory Locks | Bei horizontaler Skalierung (mehrere App-Instanzen) |
| Snapshotting | Bei sehr langen Streams (>100k Events pro Entity) |
| DotNetCore.CAP / Wolverine | Als Dispatcher, wenn externe Services benötigt werden |
| Database-per-Tenant | Bei Enterprise-Kunden mit regulatorischen Anforderungen |
| Table Partitioning | Bei >10M Events im Change Feed (Retention, Performance) |
| Schema-Registry | Bei >10 Event-Typen (zentrale Dokumentation der Payload-Schemas) |

## Betriebsinvarianten (nicht optional)

Diese Regeln gelten ab dem ersten produktiven Einsatz:

1. Projection-Handler sind idempotent.
2. At-least-once Semantik ist akzeptiert und getestet.
3. Für Poison Events existiert ein operativer Pfad (`projection_failures` Monitoring + Manual/Auto Skip).
4. Replay wird in Staging regelmäßig geprobt.
5. Neue Event-Versionen werden erst nach Consumer-Readiness ausgerollt.
6. Connection-Pool ist auf mindestens `Anzahl Projections + Headroom für HTTP-Requests` dimensioniert.
7. DSGVO-Löschungen folgen der vollständigen Checkliste (siehe [06-gdpr.md](06-gdpr.md)).
8. **`actor_id` ist Pflichtfeld** – keine Änderung ohne Akteur-Zuordnung.
9. **Eingabevalidierung** im `ChangeWriter` ist aktiv (Event-Type-Regex, Payload-Größenlimit).
10. **Row-Level Security** ist für alle Tabellen aktiviert (Multi-Tenancy).
11. **PostgreSQL ≥ 14** wird verwendet (wegen `pg_current_snapshot()` und `xmin`-Filter).
12. **In Phase 1–2 läuft genau eine App-Instanz.** Vor Multi-Instance-Deployment muss Projection Leasing implementiert werden.

---

## Monitoring und Health-Checks

### Projection-Lag überwachen

Der wichtigste operative Indikator ist der **Projection-Lag**: die Differenz zwischen dem neuesten Event im Change Feed und dem Checkpoint jeder Projection.

```sql
-- Projection-Lag für alle Projections
SELECT
    pc.projection_name,
    pc.last_sequence_id,
    cf.max_sequence_id,
    cf.max_sequence_id - pc.last_sequence_id AS lag,
    pc.updated_at AS last_activity
FROM projection_checkpoint pc
CROSS JOIN (SELECT MAX(sequence_id) AS max_sequence_id FROM change_feed) cf
ORDER BY lag DESC;
```

**Empfohlene Schwellwerte:**

| Lag | Bedeutung | Aktion |
|---|---|---|
| 0–10 | Normal | Keine |
| 10–1000 | Leicht hinterher | Beobachten |
| >1000 | Deutlich hinterher | Untersuchen (Performance, Fehler) |
| Wächst stetig | Projection kann nicht mithalten | Alarm, Ursache beheben |

### Fehler-Monitoring

```sql
-- Aktive Fehler pro Projection
SELECT
    projection_name,
    COUNT(*) AS failed_events,
    MAX(attempts) AS max_attempts,
    MIN(next_retry_at) AS next_retry
FROM projection_failures
GROUP BY projection_name;

-- Poison Events (maximale Versuche erreicht)
SELECT *
FROM projection_failures
WHERE attempts >= 10
ORDER BY updated_at DESC;
```

### Health-Check-Endpoint (Beispiel)

```csharp
// src/App/HealthChecks/ProjectionHealthCheck.cs
public class ProjectionHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly long _maxAcceptableLag;

    public ProjectionHealthCheck(NpgsqlDataSource dataSource, long maxAcceptableLag = 1000)
    {
        _dataSource = dataSource;
        _maxAcceptableLag = maxAcceptableLag;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                pc.projection_name,
                COALESCE(cf.max_seq, 0) - pc.last_sequence_id AS lag
            FROM projection_checkpoint pc
            CROSS JOIN (SELECT MAX(sequence_id) AS max_seq FROM change_feed) cf
            WHERE COALESCE(cf.max_seq, 0) - pc.last_sequence_id > @maxLag
            """;
        cmd.Parameters.AddWithValue("maxLag", _maxAcceptableLag);

        var unhealthy = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            unhealthy.Add($"{reader.GetString(0)}: lag={reader.GetInt64(1)}");
        }

        return unhealthy.Count == 0
            ? HealthCheckResult.Healthy("All projections within acceptable lag.")
            : HealthCheckResult.Degraded(
                $"Projections behind: {string.Join(", ", unhealthy)}");
    }
}
```

Registrierung:

```csharp
// Program.cs
builder.Services.AddHealthChecks()
    .AddCheck<ProjectionHealthCheck>("projections");
```

---

## Zusammenfassung: Was dieses System ist

| Aspekt | Beschreibung |
|---|---|
| **Storage** | PostgreSQL ≥ 14, JSONB – kein Event Store, kein Marten |
| **Write** | CRUD + Change Feed Append, atomar in einer Transaktion, mit Eingabevalidierung |
| **Read** | Projections bauen Read Models asynchron auf |
| **Replay** | Über Worker-Signal, kein direkter Checkpoint-Reset |
| **Migration** | Nur in Projections, nicht in Events |
| **DSGVO** | Explizite Redaktion mit Autorisierung, Audit und atomarer Löschung |
| **Multi-Tenancy** | Shared Database + RLS (Phase 1), Database-per-Tenant (Phase 3+) |
| **Sicherheit** | `actor_id` Pflichtfeld, Event-Type-Validierung, Payload-Größenlimit |
| **Skalierung** | Jede Projection ein eigener Worker, beliebig viele parallel |
| **Monitoring** | Projection-Lag, Fehler-Tracking, Health-Check-Endpoint |

> Writes commit facts. Projections converge asynchronously.
