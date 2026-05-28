# Papuma Kernel – Implementierungs-Tutorial

## Was hier gebaut wird

Dieses Tutorial beschreibt die Implementierung eines **minimal event-informierten Architektur-Kernels** auf Basis von PostgreSQL, Npgsql und .NET 10. Es ist kein klassisches Event Sourcing Framework – und das ist eine bewusste Entscheidung.

## Warum kein Marten, warum kein vollständiges Event Sourcing?

Die Ausgangslage war ein KI-generierter Abstraktionslayer über Marten, den der Entwickler nicht wirklich verstand. Das ist kein Einzelfall – es ist ein strukturelles Problem:

> Wenn du ein System nicht erklären kannst, solltest du es nicht betreiben.

Die Konsequenz: Das bestehende KI-generierte Framework wird **verworfen**. Stattdessen wird ein System gebaut, das folgende Eigenschaften hat:

- Jeder Layer ist bewusst und verständlich gebaut
- Kein versteckter Abstraktionslayer
- Im Fehlerfall vollständig debugbar
- DSGVO-Anforderungen sind explizit gelöst, nicht implizit versteckt

## Getroffene Architekturentscheidungen (Übersicht)

| Entscheidung | Gewählt | Verworfen |
|---|---|---|
| Persistenz | PostgreSQL (raw, Npgsql) | Marten, Entity Framework |
| Serialisierung | `System.Text.Json` (JSON/JSONB) | Protobuf, XML |
| Messaging | Polling Workers | Kafka, RabbitMQ, Akka |
| Schema-Evolution | Projection-seitige Interpretation | Upcasting-Pipeline |
| Projektionen | Async, entkoppelt vom Write | Synchron im Write-Transaction |
| Abstraktion | Wird erst nach Phase 3 extrahiert | Framework-first Ansatz |
| Event-Klassifikation | Change Events + Business Events getrennt | Ein Event-Typ fuer alles |

## Warum JSON statt Protobuf?

Protobuf würde Typen erzwingen und das Schema fest einschreiben – das zwingt zur Event-Migration. Mit JSON/JSONB bleibt das Schema der Events stabil, und die Interpretation wandert in die Projections. Das ist der zentrale Architektur-Trick dieses Systems.

## Dokumente in diesem Tutorial

| Datei | Inhalt |
|---|---|
| [01-konzepte.md](01-konzepte.md) | Kernkonzepte: Change Feed, Projections, CQRS-Hybrid |
| [02-datenbank.md](02-datenbank.md) | PostgreSQL-Schema: alle Tabellen und Indizes |
| [03-change-feed.md](03-change-feed.md) | `ChangeRecord`, `ChangeWriter`, `BusinessEventWriter`, `OutboxWriter` |
| [04-projections.md](04-projections.md) | `ProjectionWorker`, Checkpoints, parallele Projections |
| [05-versionierung.md](05-versionierung.md) | Versionierung ohne Upcasting, typisierte Handler |
| [06-gdpr.md](06-gdpr.md) | DSGVO-Redaktion im Event-basierten System |
| [07-projektstruktur.md](07-projektstruktur.md) | Verzeichnisstruktur, Phasenplan, Konventionen |

## Voraussetzungen

- .NET 10 SDK
- PostgreSQL (lokal oder Docker)
- NuGet-Pakete: `Npgsql`, `Npgsql.DependencyInjection`, `Dapper`, `System.Text.Json` (im SDK enthalten)

## Betriebsmodell (wichtig)

Die Projections laufen mit **at-least-once Verarbeitung**. Das bedeutet:

- Ein Event kann in Fehlerfällen erneut verarbeitet werden
- Projection-Handler müssen **idempotent** sein
- Für dauerhaft fehlerhafte Events braucht es eine Fehlerstrategie (Retry + Dead Letter)

Ohne diese drei Punkte ist das System nicht robust genug für Produktion.

## Event-Typen (wichtig)

Im Kernel gibt es zwei Event-Klassen mit unterschiedlicher Aufgabe:

- **Change Events**: beschreiben persistente Zustandsaenderungen (`change_feed`)
- **Business Events**: beschreiben fachliche Vorkommnisse, die nicht zwingend eine CRUD-Aenderung sind (z.B. `UserLoggedIn`, `CheckoutStarted`, `PaymentAuthorized`)

Diese Trennung verhindert semantische Vermischung und macht das System fuer Audit, Analytics und Integrationen deutlich klarer.
## Phasenbezug der Dokumente

- `01-konzepte.md` bis `04-projections.md`: Phase 1-2 (Foundation + reale Projections)
- `05-versionierung.md`: Phase 3 (Versioning)
- `06-gdpr.md`: Phase 3/4 (Compliance-Härtung)
- `07-projektstruktur.md`: Gesamt-Roadmap und Betriebskonventionen

## Der wichtigste Satz dieses Systems

> "We never migrate events. We evolve their meaning in projections."
