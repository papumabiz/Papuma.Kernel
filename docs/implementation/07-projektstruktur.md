# 07 - Projektstruktur, Konventionen und Phasenplan

## Library-first: Papuma.Kernel als eigenstaendiges NuGet-Package

Der Kernel wird von Anfang an als eigenstaendige Library entwickelt.
Die App referenziert den Kernel, nie umgekehrt.

Kernregel:

- Kein Feature-Code im Kernel.
- Keine UI- oder Host-Abhaengigkeit im Kernel.
- Scope-Semantik ist im Kernel verankert (siehe 09-scope-model.md).

## Aktuelle Solution-Struktur

```text
Papuma.Kernel.slnx
|
+-- src/
|   +-- Papuma.Kernel/
|   |   +-- ChangeFeed/
|   |   |   +-- ChangeRecord.cs
|   |   |   +-- ChangeFeedExtensions.cs
|   |   |   +-- ChangeFeedReader.cs
|   |   |   +-- ChangeWriter.cs
|   |   |   +-- ChangeWriterOptions.cs
|   |   +-- Events/
|   |   |   +-- BusinessEventWriter.cs
|   |   |   +-- BusinessEventWriterOptions.cs
|   |   |   +-- IOutboxPublisher.cs
|   |   |   +-- OutboxExtensions.cs
|   |   |   +-- OutboxWorker.cs
|   |   |   +-- OutboxWorkerOptions.cs
|   |   |   +-- OutboxWriter.cs
|   |   |   +-- OutboxWriterOptions.cs
|   |   +-- Gdpr/
|   |   |   +-- GdprProcessor.cs
|   |   |   +-- EntityHistory.cs
|   |   |   +-- BusinessEventRecord.cs
|   |   |   +-- RedactionResult.cs
|   |   +-- Projections/
|   |   |   +-- IProjectionHandler.cs
|   |   |   +-- IExternalProjectionHandler.cs
|   |   |   +-- IReplayableProjection.cs
|   |   |   +-- IVersionedHandler.cs
|   |   |   +-- ExternalProjectionWorker.cs
|   |   |   +-- ProjectionWorker.cs
|   |   |   +-- ProjectionWorkerOptions.cs
|   |   |   +-- ProjectionRegistry.cs
|   |   |   +-- ProjectionExtensions.cs
|   |   |   +-- ReplayService.cs
|   |   +-- Tenancy/
|   |   |   +-- ScopeType.cs
|   |   |   +-- ScopeContext.cs
|   |   |   +-- IScopeDataSourceFactory.cs
|   |   |   +-- ScopeDataSourceFactory.cs
|   |   +-- Transactions/
|   |   |   +-- IUnitOfWork.cs
|   |   |   +-- NpgsqlUnitOfWork.cs
|   |   |   +-- UnitOfWorkOptions.cs
|   |   +-- Validation/
|   |   |   +-- InputValidator.cs
|   |   +-- Schema/
|   |   |   +-- schema.sql
|   |   +-- PapumaKernelOptions.cs
|   |   +-- ServiceCollectionExtensions.cs
|   +-- Papuma.Kernel.AspNetCore/
|       +-- Projections/
|       |   +-- ProjectionHealthCheckExtensions.cs
|       |   +-- ProjectionHealthCheckOptions.cs
|       |   +-- ProjectionLagHealthCheck.cs
|       +-- Tenancy/
|       |   +-- IScopeResolver.cs
|       |   +-- ScopeMiddleware.cs
|       |   +-- ScopeMiddlewareExtensions.cs
+-- tests/
|   +-- Papuma.Kernel.Tests/
|   +-- Papuma.Kernel.AspNetCore.Tests/
+-- docs/implementation/
```

Hinweis:

- ASP.NET-Core-spezifische Typen liegen im separaten Package Papuma.Kernel.AspNetCore.
- Der Kern Papuma.Kernel bleibt host-agnostisch.

## Was gehoert wohin?

| Kernel (Papuma.Kernel) | AspNetCore (Papuma.Kernel.AspNetCore) | App (konsumierend) |
|---|---|---|
| ChangeRecord, ChangeWriter | IScopeResolver, ScopeMiddleware | Feature-Events und Handler |
| ProjectionWorker, ReplayService | ScopeMiddlewareExtensions | Projektionen pro App |
| GdprProcessor | AddPapumaScope<TResolver>() | konkrete Resolver-Implementierungen |
| ScopeContext, IScopeDataSourceFactory | UseScopeResolution() | Program.cs-Komposition |
| schema.sql | | Domain-Tabellen |

## Integration in einer konsumierenden App

```csharp
// Program.cs
builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddPapumaKernel();

builder.Services.AddProjection<UserReadModelProjection>();
builder.Services.AddReplayService();

builder.Services.AddPapumaScope<JwtClaimScopeResolver>();

var app = builder.Build();
app.UseScopeResolution();
```

## Phasenplan (aktualisiert)

### Phase 1 - Foundation

1. Schema anlegen (inkl. scope/tenant_id-Invariante)
2. RLS fuer Kernel-Tabellen aktivieren
3. ScopeContext + Resolver/Middleware integrieren
4. ChangeWriter/BusinessEventWriter/OutboxWriter mit ScopeContext nutzen
5. Erste Projection mit Checkpointing und Failure-Tracking
6. GDPR-Flow mit ScopeContext und Audit-Event

### Phase 2 - Reale Projections

1. Mehrere produktionsnahe Projection-Handler
2. Konkrete `IOutboxPublisher`-Implementierungen und Broker/Webhook-Adapter
3. Monitoring (Lag, Failure, Health)

### Phase 3 - Skalierung

1. Versionierte Handler und Replay-Tooling
2. Database-per-tenant mit IScopeDataSourceFactory
3. Retention/Partitionierung nach Bedarf

### Phase 4 - Stabilisierung

1. API-Haertung und Paketrelease
2. Betriebskonventionen finalisieren
3. Optional Hybrid-Modelle fuer unterschiedliche Kundentiers

## Wie 09 sich einfuegt

07 beschreibt Struktur und Arbeitsmodus.
09 beschreibt das verbindliche Scope-Zielmodell.

Praktische Regel:

- 07 = Organisation und Umsetzungspfad
- 09 = fachlich-technische Norm fuer Scope/tenant_id

Wenn Aussagen kollidieren, gilt 09 als Referenz.
