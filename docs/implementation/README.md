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

## Warum JSON statt Protobuf?

Protobuf würde Typen erzwingen und das Schema fest einschreiben – das zwingt zur Event-Migration. Mit JSON/JSONB bleibt das Schema der Events stabil, und die Interpretation wandert in die Projections. Das ist der zentrale Architektur-Trick dieses Systems.

## Dokumente in diesem Tutorial

| Datei | Inhalt |
|---|---|
| [01-konzepte.md](01-konzepte.md) | Kernkonzepte: Change Feed, Projections, CQRS-Hybrid |
| [02-datenbank.md](02-datenbank.md) | PostgreSQL-Schema: alle Tabellen und Indizes |
| [03-change-feed.md](03-change-feed.md) | `ChangeRecord`, `ChangeWriter`, transaktionales Schreiben |
| [04-projections.md](04-projections.md) | `ProjectionWorker`, Checkpoints, parallele Projections |
| [05-versionierung.md](05-versionierung.md) | Versionierung ohne Upcasting, typisierte Handler |
| [06-gdpr.md](06-gdpr.md) | DSGVO-Redaktion im Event-basierten System |
| [07-projektstruktur.md](07-projektstruktur.md) | Verzeichnisstruktur, Phasenplan, Konventionen |

## Voraussetzungen

- .NET 10 SDK
- PostgreSQL (lokal oder Docker)
- NuGet-Pakete: `Npgsql`, `Dapper`, `System.Text.Json` (im SDK enthalten)

## Der wichtigste Satz dieses Systems

> "We never migrate events. We evolve their meaning in projections."
