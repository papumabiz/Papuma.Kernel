# 08 - Multi-Tenancy

## Wichtiger Hinweis

Dieses Kapitel ist bewusst auf das aktuelle Scope-Modell ausgerichtet.
Die normative Detaildefinition liegt in 09-scope-model.md.

Kurzfassung:

- Scope = Platform -> tenant_id ist NULL
- Scope = Tenant -> tenant_id ist Pflicht

## Warum weiterhin Multi-Tenancy im Kern?

Multi-Tenancy spaet einzufuehren ist teuer und riskant.
Deshalb werden Isolation, Routing und Datenzugriff von Anfang an im Kernmodell verankert.

## Strategien

| Aspekt | Shared Database | Database-per-tenant |
|---|---|---|
| Isolation | logisch (scope + tenant_id + RLS) | physisch (eigene DB pro Tenant) |
| Betriebsaufwand | niedriger | hoeher |
| Skalierung | vertikal | horizontal |
| DSGVO Tenant-Loeschung | aufwaendiger | einfacher |
| Typischer Start | Phase 1 | spaeter je Bedarf |

Empfehlung:

1. Start mit Shared Database + RLS.
2. Wechsel/Ergaenzung zu Database-per-tenant nur bei klarer Notwendigkeit.

## Shared Database mit Scope

### Tabellenprinzip

Kernel-Tabellen tragen:

- scope (Platform oder Tenant)
- tenant_id (nur bei Tenant gesetzt)

Invariante wird per Check-Constraint erzwungen.

### RLS-Prinzip

Die Session setzt:

- app.current_scope
- app.current_tenant

RLS erlaubt nur Datensaetze, die zum gesetzten Scope passen.

Beispielmuster:

```sql
USING (
  (
    current_setting('app.current_scope', true) = 'Tenant'
    AND scope = 'Tenant'
    AND tenant_id = current_setting('app.current_tenant', true)
  )
  OR
  (
    current_setting('app.current_scope', true) = 'Platform'
    AND scope = 'Platform'
  )
)
```

## Resolver und Middleware (ASP.NET Core)

Aktuelle Schnittstellen:

- IScopeResolver
- ScopeMiddleware
- ScopeMiddlewareExtensions

Beispiel:

```csharp
public class JwtClaimScopeResolver : IScopeResolver
{
    public ScopeContext Resolve(HttpContext context)
    {
        var tenantId = context.User.FindFirst("tenant_id")?.Value;

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // Betreiber-Endpunkte koennen Platform sein.
            return ScopeContext.Platform();
        }

        return ScopeContext.Tenant(tenantId);
    }
}
```

Registrierung:

```csharp
builder.Services.AddPapumaScope<JwtClaimScopeResolver>();

var app = builder.Build();
app.UseScopeResolution();
```

## Writer-Nutzung mit ScopeContext

ChangeWriter, BusinessEventWriter und OutboxWriter erwarten ScopeContext.

Beispiel:

```csharp
await _changeWriter.AppendAsync(
    transaction: tx,
    scope: ScopeContext.Tenant("acme"),
    entity: "User",
    entityId: userId.ToString(),
    eventType: "UserEmailUpdated",
    version: 1,
    payloadJson: payload,
    actorId: actorId,
    ct: ct);
```

Plattformweit:

```csharp
await _businessEventWriter.AppendAsync(
    transaction: tx,
    scope: ScopeContext.Platform(),
    eventType: "PlatformConfigChanged",
    actorId: "admin:ops",
    payloadJson: payload,
    ct: ct);
```

## Projection-Strategien

### Shared Worker (haeufiger Start)

Ein Worker verarbeitet alle Scopes oder wird explizit pro Scope gestartet.

```csharp
builder.Services.AddProjection<UserReadModelProjection>(
    scope: ScopeContext.Tenant("acme"));

builder.Services.AddProjection<PlatformAuditProjection>(
    scope: ScopeContext.Platform());
```

### Trennung pro Scope

Sinnvoll bei hohen Lastunterschieden oder klaren Betriebsgrenzen.

## Database-per-tenant

Bei dieser Strategie wird pro Tenant eine eigene Datenbank genutzt.

Aktuelle Abstraktion:

- IScopeDataSourceFactory
- ScopeDataSourceFactory

Hinweis:

- Die Factory akzeptiert ScopeContext, aber nur Scope = Tenant ist gueltig.
- Platform-Scope in einer tenant-spezifischen Factory ist ein Fehlerfall.

Beispiel:

```csharp
public class UpdateUserEmailHandler
{
    private readonly IScopeDataSourceFactory _factory;

    public async Task HandleAsync(ScopeContext scope, Guid userId, string email, CancellationToken ct)
    {
        var ds = _factory.GetDataSource(scope); // erwartet Tenant-Scope
        await using var conn = await ds.OpenConnectionAsync(ct);
        // ...
    }
}
```

## DSGVO im Multi-Tenant-Betrieb

GDPR-Operationen laufen immer mit ScopeContext.

- Tenant-bezogene Redaktion: ScopeContext.Tenant("...")
- Plattformweite Betreiberdaten: ScopeContext.Platform()

Entscheidend ist, dass Scope im Datenmodell und in den Queries explizit gefiltert wird.

## Betriebsempfehlungen

1. Default in Phase 1: Shared Database + RLS + ScopeResolver.
2. Scope explizit je Endpoint und Use Case festlegen.
3. Keine impliziten Default-Tenants verwenden.
4. Projection-Lag getrennt nach Scope beobachten.
5. Database-per-tenant nur mit belastbarer Betriebsnotwendigkeit einfuehren.

## Bezug zu 09

08 beschreibt Betriebsstrategien.
09 beschreibt die verbindliche Modellinvariante und API-Richtung.

Bei Detailfragen zu Constraints, RLS-Policies oder API-Signaturen gilt 09 als Referenz.
