# 08 – Multi-Tenancy

## Warum Multi-Tenancy ab Phase 1?

Multi-Tenancy nachträglich einzuführen ist einer der teuersten Architektur-Umbauten. Wenn das System von Anfang an mandantenfähig sein soll, muss die Tenant-Isolation **in den Kern** eingebaut werden – nicht als nachträglicher Layer.

> **Regel:** Jede Datenbankzeile, jedes Event und jede Projection muss einem Tenant zugeordnet werden können. Ausnahmen (systemweite Daten) müssen explizit als solche markiert sein.

---

## Zwei Strategien: Database-per-Tenant vs. Shared Database

| Aspekt | Database-per-Tenant | Shared Database (Discriminator) |
|---|---|---|
| **Isolation** | Vollständig (eigene DB) | Logisch (Spalte `tenant_id`) |
| **DSGVO** | Einfach: DB löschen = alles weg | Aufwändiger: alle Tabellen filtern |
| **Skalierung** | Horizontal (neue DB = neuer Tenant) | Vertikal (eine DB für alle) |
| **Betriebsaufwand** | Hoch (N Datenbanken verwalten) | Niedrig (eine DB) |
| **Kosten** | Höher (Connection-Pools pro DB) | Niedriger |
| **Cross-Tenant-Queries** | Schwierig bis unmöglich | Einfach |
| **Geeignet für** | Enterprise-Kunden, regulierte Branchen | SaaS, kleine bis mittlere Projekte |

### Empfehlung

**Beide Strategien werden unterstützt.** Die Wahl erfolgt pro Deployment:

- **Kleine Projekte / SaaS:** Shared Database mit `tenant_id`-Discriminator
- **Enterprise / regulierte Branchen:** Database-per-Tenant

Der Kernel abstrahiert die Tenant-Auflösung über ein `ITenantResolver`-Interface. Der restliche Code arbeitet immer mit einem aufgelösten `TenantContext`.

---

## Strategie 1: Shared Database (Discriminator)

### Schema-Erweiterungen

Alle relevanten Tabellen erhalten eine `tenant_id`-Spalte:

```sql
-- change_feed: tenant_id hinzufügen
ALTER TABLE change_feed ADD COLUMN tenant_id TEXT NOT NULL;
CREATE INDEX idx_change_feed_tenant ON change_feed (tenant_id, sequence_id);

-- business_event_log: tenant_id hinzufügen
ALTER TABLE business_event_log ADD COLUMN tenant_id TEXT NOT NULL;
CREATE INDEX idx_business_event_tenant ON business_event_log (tenant_id, occurred_at);

-- projection_checkpoint und projection_failures:
-- Kein tenant_id nötig. Der ProjectionWorker kodiert den Tenant in den
-- Projection-Namen: "handler_name@tenant_id". Damit bleibt das Schema
-- einfach und der Primary Key unverändert.
-- Beispiel: projection_name = "user_read_model@acme-corp"

-- event_outbox: tenant_id hinzufügen
ALTER TABLE event_outbox ADD COLUMN tenant_id TEXT NOT NULL;
CREATE INDEX idx_event_outbox_tenant ON event_outbox (tenant_id, status, next_retry_at);

-- domain: users (Beispiel)
ALTER TABLE users ADD COLUMN tenant_id TEXT NOT NULL;
CREATE INDEX idx_users_tenant ON users (tenant_id);
```

> ⚠️ **Wichtig: Row-Level Security (RLS)**
>
> Bei Shared Database ist RLS **dringend empfohlen**, um versehentliche Cross-Tenant-Zugriffe auf Datenbankebene zu verhindern:
>
> ```sql
> ALTER TABLE change_feed ENABLE ROW LEVEL SECURITY;
> CREATE POLICY tenant_isolation ON change_feed
>     USING (tenant_id = current_setting('app.current_tenant'));
> ```
>
> Die App setzt `SET LOCAL app.current_tenant = 'tenant-xyz'` am Anfang jeder Transaktion. Damit ist die Isolation auch bei Programmierfehlern gewährleistet.

### Vollständiges Init-Skript (Shared Database)

```sql
-- change_feed (mit tenant_id)
CREATE TABLE change_feed (
    sequence_id    BIGSERIAL   PRIMARY KEY,
    tenant_id      TEXT        NOT NULL,
    entity         TEXT        NOT NULL,
    entity_id      TEXT        NOT NULL,
    event_type     TEXT        NOT NULL,
    version        INT         NOT NULL DEFAULT 1,
    correlation_id TEXT        NULL,
    causation_id   TEXT        NULL,
    actor_id       TEXT        NOT NULL,
    payload        JSONB       NOT NULL,
    timestamp      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted       BOOLEAN     NOT NULL DEFAULT FALSE
);

CREATE INDEX idx_change_feed_tenant        ON change_feed (tenant_id, sequence_id);
CREATE INDEX idx_change_feed_entity_id     ON change_feed (tenant_id, entity, entity_id);
CREATE INDEX idx_change_feed_event_type    ON change_feed (tenant_id, event_type);
CREATE INDEX idx_change_feed_not_redacted  ON change_feed (tenant_id, sequence_id) WHERE redacted = FALSE;
CREATE INDEX idx_change_feed_correlation   ON change_feed (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX idx_change_feed_actor         ON change_feed (actor_id);

-- Row-Level Security
ALTER TABLE change_feed ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_change_feed ON change_feed
    USING (tenant_id = current_setting('app.current_tenant'));
```

### `TenantContext` und `ITenantResolver`

```csharp
// src/Kernel/Tenancy/TenantContext.cs
public record TenantContext(string TenantId)
{
    /// <summary>
    /// Validiert die Tenant-ID. Muss denselben Regeln folgen wie entity/eventType.
    /// </summary>
    public static TenantContext Create(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 100)
            throw new ArgumentException("tenantId must not be empty and max 100 characters.");
        return new TenantContext(tenantId);
    }
}

// src/Kernel/Tenancy/ITenantResolver.cs
public interface ITenantResolver
{
    /// <summary>
    /// Löst den aktuellen Tenant aus dem Request-Context auf.
    /// Implementierungen: Header-basiert, Subdomain-basiert, JWT-Claim-basiert, etc.
    /// </summary>
    TenantContext Resolve(HttpContext context);
}
```

### Sicherheit der Tenant-Auflösung (kritisch)

> ⚠️ **Die Tenant-Auflösung ist die wichtigste Sicherheitsentscheidung im Multi-Tenancy-System.** Ein manipulierter Tenant-Header ermöglicht Cross-Tenant-Zugriff auf alle Daten.

#### Wann ist welcher Resolver sicher?

| Resolver | Sicher? | Anwendungsfall |
|---|---|---|
| **JWT-Claim** (`tenant_id` im Token) | ✅ **Empfohlen** | SPAs, Mobile Apps, API-Clients – der Tenant ist kryptografisch im Token signiert |
| **Header** (`X-Tenant-Id`) | ⚠️ **Nur hinter vertrauenswürdigem Gateway** | Service-to-Service-Kommunikation, wo ein API-Gateway den Header setzt und validiert |
| **Subdomain** (`tenant-a.app.example.com`) | ✅ Sicher | SaaS mit Subdomain-Routing (DNS + Reverse-Proxy kontrollieren die Zuordnung) |
| **Session** (serverseitige Session) | ✅ Sicher | Klassische Server-Rendered Apps (MVC, Razor Pages) |

#### Warum ist der Header-Resolver allein unsicher?

Ein HTTP-Header kann von **jedem Client** beliebig gesetzt werden:

```
# Angreifer setzt fremden Tenant:
curl -H "X-Tenant-Id: fremder-tenant" https://api.example.com/users
```

Ohne weitere Validierung sieht der Server den Request als `fremder-tenant` und gibt dessen Daten zurück. **Das ist ein vollständiger Tenant-Escape.**

#### Die sichere Lösung: JWT-Claim-basierte Auflösung

Bei SPAs und API-Clients ist der **JWT-Claim** die richtige Strategie. Der Tenant wird beim Login in den Token geschrieben und ist kryptografisch signiert – der Client kann ihn nicht manipulieren:

```
JWT Payload:
{
  "sub": "user:550e8400-...",
  "tenant_id": "acme-corp",
  "role": "admin",
  "exp": 1735689600
}
```

Der Server extrahiert den Tenant aus dem **validierten** Token, nicht aus einem Header:

```csharp
// src/App/Tenancy/JwtClaimTenantResolver.cs – EMPFOHLEN für SPAs/APIs
public class JwtClaimTenantResolver : ITenantResolver
{
    public TenantContext Resolve(HttpContext context)
    {
        // context.User ist bereits durch die JWT-Middleware validiert und signaturgeprüft.
        // Der Claim kann nicht vom Client manipuliert werden.
        var tenantId = context.User.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim in JWT.");
        return TenantContext.Create(tenantId);
    }
}
```

**Warum ist das sicher?**
1. Der JWT wird vom Identity Provider (z.B. Keycloak, Auth0, eigener AuthServer) signiert
2. Die ASP.NET Core JWT-Middleware validiert die Signatur **vor** dem Resolver
3. Der Client kann den `tenant_id`-Claim nicht ändern, ohne die Signatur zu brechen
4. Der Tenant ist an die Authentifizierung gekoppelt – kein Zugriff ohne gültigen Token

#### Wann ist der Header-Resolver akzeptabel?

Nur in **zwei** Szenarien:

**1. Service-to-Service hinter einem API-Gateway:**
```
Client → API-Gateway (validiert JWT, setzt X-Tenant-Id) → Backend-Service (liest Header)
```
Das Gateway ist vertrauenswürdig und setzt den Header basierend auf dem validierten Token. Der Backend-Service akzeptiert den Header, weil er nur vom Gateway erreichbar ist (Netzwerk-Isolation).

**2. Lokale Entwicklung / Tests:**
Für schnelles Testen ohne JWT-Setup. **Niemals in Produktion.**

```csharp
// src/App/Tenancy/HeaderTenantResolver.cs – NUR für Development/Testing
public class HeaderTenantResolver : ITenantResolver
{
    public TenantContext Resolve(HttpContext context)
    {
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault()
            ?? throw new UnauthorizedAccessException("Missing X-Tenant-Id header.");
        return TenantContext.Create(tenantId);
    }
}
```

```csharp
// Program.cs – Resolver je nach Environment wählen
if (builder.Environment.IsDevelopment())
    builder.Services.AddScoped<ITenantResolver, HeaderTenantResolver>();
else
    builder.Services.AddScoped<ITenantResolver, JwtClaimTenantResolver>();
```

#### Und die serverseitige Session?

Bei klassischen Server-Rendered Apps (Razor Pages, MVC mit Cookie-Auth) ist der Tenant typischerweise in der Session oder im Authentication-Cookie gespeichert. Das ist sicher, weil die Session serverseitig verwaltet wird:

```csharp
// src/App/Tenancy/SessionTenantResolver.cs – für Server-Rendered Apps
public class SessionTenantResolver : ITenantResolver
{
    public TenantContext Resolve(HttpContext context)
    {
        // Tenant aus dem authentifizierten User-Claim (Cookie-Auth)
        var tenantId = context.User.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id in session.");
        return TenantContext.Create(tenantId);
    }
}
```

> **Fazit:** In der Praxis ist `JwtClaimTenantResolver` und `SessionTenantResolver` technisch identisch – beide lesen aus `context.User`. Der Unterschied liegt in der Authentifizierungsmethode (JWT-Bearer vs. Cookie), nicht im Resolver.

#### Zusammenfassung: Empfohlene Konfiguration

| Szenario | Resolver | Auth-Methode |
|---|---|---|
| SPA + API | `JwtClaimTenantResolver` | JWT Bearer Token |
| Mobile App + API | `JwtClaimTenantResolver` | JWT Bearer Token |
| Server-Rendered (MVC/Razor) | `JwtClaimTenantResolver` | Cookie Auth (Claim im Cookie) |
| Service-to-Service | `HeaderTenantResolver` | mTLS + Gateway-Validierung |
| Lokale Entwicklung | `HeaderTenantResolver` | Kein Auth |

#### Zusätzliche Absicherung: Cross-Check mit RLS

Selbst wenn der Resolver korrekt arbeitet, bietet Row-Level Security eine **zweite Verteidigungslinie**:

```sql
-- RLS prüft auf DB-Ebene, unabhängig vom Application-Code
ALTER TABLE change_feed ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON change_feed
    USING (tenant_id = current_setting('app.current_tenant'));
```

Wenn ein Programmierfehler den falschen Tenant-Context setzt, verhindert RLS trotzdem den Cross-Tenant-Zugriff – vorausgesetzt, `SET LOCAL app.current_tenant` wird korrekt am Anfang jeder Transaktion gesetzt.

### Middleware: Tenant-Context setzen

```csharp
// src/Kernel/Tenancy/TenantMiddleware.cs
public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver)
    {
        var tenant = resolver.Resolve(context);
        context.Items["TenantContext"] = tenant;
        await _next(context);
    }
}

// Extension für einfache Registrierung
public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantMiddleware>();

    public static TenantContext GetTenantContext(this HttpContext context)
        => context.Items["TenantContext"] as TenantContext
           ?? throw new InvalidOperationException("TenantContext not set. Is TenantMiddleware registered?");
}
```

### `ChangeWriter` mit Tenant-Support

```csharp
// src/Kernel/ChangeFeed/ChangeWriter.cs (erweitert)
public async Task AppendAsync(
    NpgsqlTransaction transaction,
    TenantContext tenant,
    string entity,
    string entityId,
    string eventType,
    int version,
    string payloadJson,
    string actorId,
    string? correlationId = null,
    string? causationId = null,
    CancellationToken ct = default)
{
    ValidateInputs(entity, entityId, eventType, version, payloadJson, actorId);

    // RLS: Tenant-Context auf Connection setzen
    await SetTenantOnConnection(transaction.Connection!, tenant.TenantId, ct);

    await using var cmd = transaction.Connection!.CreateCommand();
    cmd.Transaction = transaction;
    cmd.CommandText = """
        INSERT INTO change_feed
            (tenant_id, entity, entity_id, event_type, version,
             correlation_id, causation_id, actor_id, payload)
        VALUES
            (@tenantId, @entity, @entityId, @eventType, @version,
             @correlationId, @causationId, @actorId, @payload::jsonb)
        """;

    cmd.Parameters.AddWithValue("tenantId",      tenant.TenantId);
    cmd.Parameters.AddWithValue("entity",        entity);
    cmd.Parameters.AddWithValue("entityId",      entityId);
    cmd.Parameters.AddWithValue("eventType",     eventType);
    cmd.Parameters.AddWithValue("version",       version);
    cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("causationId",   (object?)causationId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("actorId",       actorId);
    cmd.Parameters.AddWithValue("payload",       payloadJson);

    await cmd.ExecuteNonQueryAsync(ct);
}

private static async Task SetTenantOnConnection(
    NpgsqlConnection conn, string tenantId, CancellationToken ct)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SET LOCAL app.current_tenant = @tenantId";
    cmd.Parameters.AddWithValue("tenantId", tenantId);
    await cmd.ExecuteNonQueryAsync(ct);
}
```

### Projection Worker mit Tenant-Support

Bei Shared Database gibt es zwei Optionen für Projection Workers:

**Option A: Ein Worker pro Projection (alle Tenants)**

Der Worker verarbeitet Events aller Tenants. Einfach, aber keine Tenant-Isolation bei der Verarbeitung.

```csharp
// LoadChangesAsync: tenant_id wird mitgeladen und an den Handler übergeben
cmd.CommandText = """
    SELECT sequence_id, tenant_id, entity, entity_id, event_type, ...
    FROM change_feed
    WHERE sequence_id > @lastSeen
      AND redacted = FALSE
      AND event_type = ANY(@eventTypes)
      ...
    ORDER BY sequence_id
    LIMIT @batchSize
    """;
```

**Option B: Ein Worker pro Tenant pro Projection**

Maximale Isolation, aber mehr Ressourcenverbrauch. Sinnvoll bei wenigen, großen Tenants.

```csharp
// Pro Tenant einen eigenen Worker starten
foreach (var tenantId in activeTenants)
{
    services.AddSingleton<IHostedService>(sp => new ProjectionWorker(
        handler: sp.GetRequiredService<UserReadModelProjection>(),
        dataSource: sp.GetRequiredService<NpgsqlDataSource>(),
        logger: sp.GetRequiredService<ILogger<ProjectionWorker>>(),
        tenantId: tenantId  // Worker filtert nur Events dieses Tenants
    ));
}
```

**Empfehlung:** In Phase 1 mit **Option A** starten (ein Worker, alle Tenants). Option B erst bei nachgewiesenem Bedarf (z.B. ein Tenant erzeugt 90% des Event-Volumens und blockiert andere).

---

## Strategie 2: Database-per-Tenant

### Architektur

```
┌─────────────────────────────────────────┐
│            Tenant Router                │
│  (löst Tenant → Connection String auf)  │
└─────────────────────────────────────────┘
         │              │              │
    ┌────▼────┐    ┌────▼────┐    ┌────▼────┐
    │ DB:     │    │ DB:     │    │ DB:     │
    │ tenant_a│    │ tenant_b│    │ tenant_c│
    └─────────┘    └─────────┘    └─────────┘
```

Jeder Tenant hat eine eigene PostgreSQL-Datenbank mit identischem Schema (ohne `tenant_id`-Spalte, da die Isolation auf DB-Ebene erfolgt).

### `ITenantDataSourceFactory`

```csharp
// src/Kernel/Tenancy/ITenantDataSourceFactory.cs
public interface ITenantDataSourceFactory
{
    /// <summary>
    /// Gibt die NpgsqlDataSource für einen bestimmten Tenant zurück.
    /// Implementierungen cachen DataSources pro Tenant (Connection-Pool-Reuse).
    /// </summary>
    NpgsqlDataSource GetDataSource(TenantContext tenant);
}

// src/Kernel/Tenancy/TenantDataSourceFactory.cs
public class TenantDataSourceFactory : ITenantDataSourceFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _dataSources = new();
    private readonly Func<string, string> _connectionStringResolver;

    public TenantDataSourceFactory(Func<string, string> connectionStringResolver)
    {
        _connectionStringResolver = connectionStringResolver;
    }

    public NpgsqlDataSource GetDataSource(TenantContext tenant)
    {
        return _dataSources.GetOrAdd(tenant.TenantId, tenantId =>
        {
            var connectionString = _connectionStringResolver(tenantId);
            return NpgsqlDataSource.Create(connectionString);
        });
    }

    public void Dispose()
    {
        foreach (var ds in _dataSources.Values)
            ds.Dispose();
    }
}
```

### Connection-String-Auflösung

```csharp
// Beispiel: Connection-Strings aus Konfiguration
var factory = new TenantDataSourceFactory(tenantId =>
{
    // Option 1: Aus appsettings.json
    return configuration.GetConnectionString($"Tenant_{tenantId}")
        ?? throw new InvalidOperationException($"No connection string for tenant '{tenantId}'.");

    // Option 2: Konvention (gleicher Server, DB-Name = Tenant-ID)
    // return $"Host=localhost;Database=papuma_{tenantId};Username=postgres;Password=secret";

    // Option 3: Aus einer Tenant-Registry-Tabelle (zentrale Management-DB)
    // return tenantRegistry.GetConnectionString(tenantId);
});
```

### Use Case mit Database-per-Tenant

```csharp
public class UpdateUserEmailHandler
{
    private readonly ITenantDataSourceFactory _tenantFactory;
    private readonly ChangeWriter _changeWriter;

    public async Task HandleAsync(
        TenantContext tenant,
        Guid userId,
        string newEmail,
        string actorId,
        CancellationToken ct = default)
    {
        var dataSource = _tenantFactory.GetDataSource(tenant);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // CRUD + Change Feed – identisch wie ohne Multi-Tenancy
        // (kein tenant_id in SQL nötig, da eigene DB)
        await using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = """
            UPDATE users SET email = @email, updated_at = NOW()
            WHERE id = @id
            """;
        updateCmd.Parameters.AddWithValue("email", newEmail);
        updateCmd.Parameters.AddWithValue("id", userId);
        await updateCmd.ExecuteNonQueryAsync(ct);

        var payload = JsonSerializer.Serialize(new { Email = newEmail });
        await _changeWriter.AppendAsync(
            transaction: tx,
            entity: "User",
            entityId: userId.ToString(),
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: payload,
            actorId: actorId,
            ct: ct);

        await tx.CommitAsync(ct);
    }
}
```

### Projection Workers bei Database-per-Tenant

Jeder Tenant braucht eigene Worker-Instanzen:

```csharp
// Dynamische Worker-Registrierung
public class TenantProjectionHostedService : IHostedService
{
    private readonly ITenantDataSourceFactory _factory;
    private readonly IEnumerable<string> _tenantIds;
    private readonly List<ProjectionWorker> _workers = new();

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var tenantId in _tenantIds)
        {
            var tenant = TenantContext.Create(tenantId);
            var dataSource = _factory.GetDataSource(tenant);

            var worker = new ProjectionWorker(
                handler: new UserReadModelProjection(),
                dataSource: dataSource,
                logger: _loggerFactory.CreateLogger<ProjectionWorker>()
            );

            _workers.Add(worker);
            await worker.StartAsync(ct);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var worker in _workers)
            await worker.StopAsync(ct);
    }
}
```

> ⚠️ **Connection-Pool-Dimensionierung:** Bei Database-per-Tenant hat jeder Tenant seinen eigenen Connection-Pool. Bei 50 Tenants mit je 3 Projections und Default-Pool-Size 100 wären das 50 × 100 = 5000 potenzielle Connections. Die Pool-Size muss pro Tenant angepasst werden (z.B. `Max Pool Size=20`).

---

## DSGVO und Multi-Tenancy

### Database-per-Tenant

DSGVO-Löschung eines gesamten Tenants ist trivial:

```sql
DROP DATABASE papuma_tenant_xyz;
```

Für einzelne Entities innerhalb eines Tenants: `GdprProcessor` wie in [06-gdpr.md](06-gdpr.md) beschrieben.

### Shared Database

Der `GdprProcessor` muss den `tenant_id` berücksichtigen:

```csharp
// RedactEntityAsync erweitert um TenantContext
public async Task<RedactionResult> RedactEntityAsync(
    TenantContext tenant,
    string entity,
    string entityId,
    string actorId,
    string reason,
    CancellationToken ct = default)
{
    // ... wie bisher, aber mit tenant_id-Filter:
    // WHERE tenant_id = @tenantId AND entity = @entity AND entity_id = @entityId
}
```

Für die Löschung eines gesamten Tenants (Shared Database):

```sql
-- Alle Daten eines Tenants redacten
UPDATE change_feed
SET payload = '{"redacted": true}'::jsonb, redacted = TRUE
WHERE tenant_id = @tenantId;

UPDATE business_event_log
SET payload = '{"redacted": true}'::jsonb, redacted = TRUE
WHERE tenant_id = @tenantId;

-- Domain-Tabellen
DELETE FROM users WHERE tenant_id = @tenantId;
-- ... weitere Tabellen
```

---

## Entscheidungshilfe: Welche Strategie wann?

```
Brauche ich vollständige Datenisolation?
  │
  ├─ JA → Regulierte Branche? Große Enterprise-Kunden?
  │         │
  │         ├─ JA → Database-per-Tenant
  │         └─ NEIN → Shared Database mit RLS reicht meistens
  │
  └─ NEIN → Shared Database (Discriminator)
```

### Hybrid-Ansatz

Für SaaS-Produkte mit unterschiedlichen Kundengrößen:

- **Free/Standard-Tier:** Shared Database
- **Enterprise-Tier:** Database-per-Tenant

Der `ITenantDataSourceFactory` abstrahiert die Unterscheidung. Der restliche Code bleibt identisch.

---

## Registrierung in ASP.NET Core

### Shared Database

```csharp
// Program.cs
builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddScoped<ITenantResolver, JwtClaimTenantResolver>();
builder.Services.AddSingleton<ChangeWriter>();

var app = builder.Build();
app.UseTenantResolution();
```

### Database-per-Tenant

```csharp
// Program.cs
builder.Services.AddSingleton<ITenantDataSourceFactory>(sp =>
    new TenantDataSourceFactory(tenantId =>
        sp.GetRequiredService<IConfiguration>()
          .GetConnectionString($"Tenant_{tenantId}")
        ?? throw new InvalidOperationException($"No connection string for tenant '{tenantId}'.")));

builder.Services.AddScoped<ITenantResolver, JwtClaimTenantResolver>();
builder.Services.AddSingleton<ChangeWriter>();

var app = builder.Build();
app.UseTenantResolution();
```

---

## Was in Phase 1 gebaut wird

| Komponente | Phase 1 | Später |
|---|---|---|
| `TenantContext` | ✅ | |
| `ITenantResolver` | ✅ (`JwtClaimTenantResolver` für Produktion, `HeaderTenantResolver` nur für Dev) | Subdomain-basiert |
| `TenantMiddleware` | ✅ | |
| `tenant_id` in allen Tabellen | ✅ (Shared Database) | |
| Row-Level Security | ✅ | |
| `ChangeWriter` mit Tenant | ✅ | |
| Database-per-Tenant | | ✅ (Phase 3+) |
| `ITenantDataSourceFactory` | | ✅ (Phase 3+) |
| Hybrid-Ansatz | | ✅ (Phase 4+) |
| Dynamische Worker pro Tenant | | ✅ (Phase 3+) |

> **Empfehlung:** In Phase 1 mit Shared Database + RLS + `JwtClaimTenantResolver` starten. Das deckt 90% der Anwendungsfälle ab. `HeaderTenantResolver` nur für lokale Entwicklung verwenden. Database-per-Tenant wird erst relevant, wenn Enterprise-Kunden mit regulatorischen Anforderungen hinzukommen.

---

## Zusammenfassung

| Aspekt | Shared Database | Database-per-Tenant |
|---|---|---|
| **Implementierungsaufwand** | Niedrig (Spalte + RLS) | Mittel (Factory + Connection-Routing) |
| **Betriebsaufwand** | Niedrig | Hoch (N Datenbanken) |
| **Isolation** | Logisch (RLS) | Physisch (eigene DB) |
| **DSGVO-Tenant-Löschung** | Aufwändig (alle Tabellen) | Trivial (`DROP DATABASE`) |
| **Skalierung** | Vertikal | Horizontal |
| **Phase 1** | ✅ Empfohlen | ❌ Später |
