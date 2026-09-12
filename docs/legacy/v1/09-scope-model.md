# 09 - Scope-Modell fuer Plattform und Tenant

## Ziel

Das Framework fuehrt ein explizites Scope-Modell ein.

Ein Datensatz ist entweder:

- Plattform-weit (Scope = Platform)
- Tenant-gebunden (Scope = Tenant)

Die tenant_id wird nicht mehr als impliziter Scope-Ersatz verwendet.

## Designprinzip

Scope ist ein fachliches Sicherheitsmerkmal und muss in jedem relevanten Datensatz explizit vorhanden sein.

Invariante:

1. Scope = Tenant -> tenant_id ist verpflichtend.
2. Scope = Platform -> tenant_id ist NULL.

Diese Invariante wird in C# und in SQL erzwungen.

## Ziel-API (Breaking Change)

### ScopeType

    namespace Papuma.Kernel.Tenancy;

    public enum ScopeType
    {
        Platform = 0,
        Tenant = 1,
    }

### ScopeContext

    namespace Papuma.Kernel.Tenancy;

    public sealed record ScopeContext
    {
        public ScopeType Scope { get; }
        public string? TenantId { get; }

        private ScopeContext(ScopeType scope, string? tenantId)
        {
            Scope = scope;
            TenantId = tenantId;
        }

        public static ScopeContext Platform() => new(ScopeType.Platform, null);

        public static ScopeContext Tenant(string tenantId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            return new ScopeContext(ScopeType.Tenant, tenantId);
        }
    }

### Writer-Signaturen

Alle Write-APIs erhalten genau ein ScopeContext-Argument.

Beispiele:

    Task AppendAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        ...);

    Task<Guid> AppendAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        ...);

    Task EnqueueAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        ...);

Keine impliziten Default-Tenants.

## Datenbankmodell

Die Tabellen change_feed, business_event_log und event_outbox erhalten eine neue Spalte scope.

Empfehlung: scope als TEXT mit CHECK-Constraint.

    ALTER TABLE change_feed
      ADD COLUMN scope TEXT NOT NULL
      CHECK (scope IN ('Platform', 'Tenant'));

    ALTER TABLE business_event_log
      ADD COLUMN scope TEXT NOT NULL
      CHECK (scope IN ('Platform', 'Tenant'));

    ALTER TABLE event_outbox
      ADD COLUMN scope TEXT NOT NULL
      CHECK (scope IN ('Platform', 'Tenant'));

Invarianten als SQL-Constraint:

    ALTER TABLE change_feed
      ADD CONSTRAINT ck_change_feed_scope_tenant
      CHECK (
        (scope = 'Platform' AND tenant_id IS NULL)
        OR
        (scope = 'Tenant' AND tenant_id IS NOT NULL)
      );

    ALTER TABLE business_event_log
      ADD CONSTRAINT ck_business_event_scope_tenant
      CHECK (
        (scope = 'Platform' AND tenant_id IS NULL)
        OR
        (scope = 'Tenant' AND tenant_id IS NOT NULL)
      );

    ALTER TABLE event_outbox
      ADD CONSTRAINT ck_event_outbox_scope_tenant
      CHECK (
        (scope = 'Platform' AND tenant_id IS NULL)
        OR
        (scope = 'Tenant' AND tenant_id IS NOT NULL)
      );

Damit wird eine semantisch ungueltige Kombination technisch ausgeschlossen.

## RLS-Strategie

RLS darf nicht im Fehlerfall unbemerkt alle Daten freigeben.

Empfohlen werden zwei Session-Variablen:

- app.current_scope
- app.current_tenant

Prinzip:

- Bei Scope Tenant sieht man nur Tenant-Daten mit passender tenant_id.
- Bei Scope Platform sieht man nur Platform-Daten.

Beispiel fuer change_feed:

    DROP POLICY IF EXISTS tenant_isolation_change_feed ON change_feed;

    CREATE POLICY scope_isolation_change_feed ON change_feed
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
    WITH CHECK (
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
    );

Wichtig:

Wenn app.current_scope nicht gesetzt ist, soll kein Zugriff moeglich sein.

## Projection und Replay

ProjectionWorker und Replay muessen Scope immer mitfuehren.

Empfehlung fuer ProjectionName:

- Tenant: {HandlerName}@Tenant:{TenantId}
- Platform: {HandlerName}@Platform

Damit bleiben Checkpoints eindeutig pro Scope.

## Outbox und Publishing

Outbox-Eintraege tragen ebenfalls scope und tenant_id gemaess Invariante.

Publisher kann damit gezielt unterscheiden:

- Tenant-Topics oder Tenant-Routing
- Platform-Topics fuer Betreiberfunktionen

## ASP.NET Core Resolver

Der Resolver liefert nicht mehr TenantContext, sondern ScopeContext.

Beispielinterface:

    public interface IScopeResolver
    {
        ScopeContext Resolve(HttpContext context);
    }

Regeln:

- Tenant-Endpunkte duerfen nur Tenant-Scope aufloesen.
- Plattform-Endpunkte duerfen nur Platform-Scope aufloesen.
- Kein stilles Fallback auf Default-Werte.

## Auswirkungen auf GDPR/History

BusinessEventRecord und aehnliche Rueckgabeobjekte sollten scope und optional tenant_id enthalten.

Nur so bleibt die Herkunft eines Ereignisses in Audits eindeutig.

## Was andere Plattformen typischerweise machen

1. Fruehe SaaS-Produkte nutzen oft einen Reserved Tenant wie default oder system.
2. Reifere Plattformen wechseln meist zu explizitem Kontext: scope + optional tenant.
3. Stark regulierte Systeme trennen Plattform und Tenant sogar physisch (eigene Streams/Tabellen/DB).

Fuer Papuma.Kernel ist explizites Scope-Modell mit gemeinsamer Datenbank ein sehr guter Mittelweg:

- klarere Semantik
- geringes Leck-Risiko
- gute Betriebsfaehigkeit

## Empfohlene Umsetzungsreihenfolge

1. ScopeType und ScopeContext einfuehren.
2. TenantContext entfernen und Aufrufer auf ScopeContext umstellen.
3. Writer-APIs auf ScopeContext umstellen.
4. Schema und Constraints erweitern.
5. RLS auf Scope-basiertes Modell umstellen.
6. ProjectionWorker/Replay auf Scope-Namensraum umstellen.
7. GDPR/History DTOs um Scope erweitern.
8. Tests fuer gueltige/ungueltige Scope-Tenant-Kombinationen ergänzen.

## Tests (Pflichtfaelle)

1. Tenant-Write mit gueltiger tenant_id funktioniert.
2. Tenant-Write ohne tenant_id scheitert.
3. Platform-Write mit tenant_id scheitert.
4. Platform-Write ohne tenant_id funktioniert.
5. RLS Tenant liest keine Platform-Daten.
6. RLS Platform liest keine Tenant-Daten.
7. Projection-Checkpoint ist pro Scope getrennt.
8. Outbox-Routing unterscheidet Tenant und Platform korrekt.
