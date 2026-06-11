# 01 – Kernkonzepte und Architektur

## Das Modell im Überblick

Dieses System ist kein reines Event Sourcing – und kein normales CRUD-System. Es ist ein **CRUD-Truth + Change-Feed-Hybrid**, der gezielt die besten Eigenschaften beider Welten kombiniert.

```
HTTP Request
    ↓
CRUD + Change Feed Append  ← alles in einer Transaktion
    ↓
COMMIT
    ↓
HTTP 200
          (asynchron, danach)
Projection Workers konsumieren Feed
```

## Was ist falsch am klassischen CRUD?

Klassisches CRUD hat keine Historie. Man weiß nie, was sich wann verändert hat. Auditing ist nachträglich aufgesetzt, Replay ist unmöglich.

## Was ist falsch an purem Event Sourcing?

Pures Event Sourcing (à la Marten/EventStore) ist:

- **Komplex**: Events als einzige Source of Truth erfordert tiefes Verständnis von Projektionen, Snapshots, Upcasting
- **DSGVO-problematisch**: Immutable Events kollidieren mit dem Recht auf Löschung
- **Schwer debugbar**: Zustand ergibt sich nur durch Replay, nicht direkt lesbar

## Die Lösung: CRUD Truth + Change Feed

```
┌─────────────────────────────────────────┐
│            Write Path                   │
│                                         │
│  CRUD-Mutation (z.B. users Tabelle)     │
│         +                               │
│  Change Feed Append (change_feed)       │
│         ↓                               │
│    COMMIT (atomar)                      │
└─────────────────────────────────────────┘
                    │
                    │ asynchron
                    ▼
┌─────────────────────────────────────────┐
│           Projection Workers            │
│                                         │
│  UserSearchProjection   (Checkpoint A)  │
│  AnalyticsProjection    (Checkpoint B)  │
│  NotificationProjection (Checkpoint C)  │
└─────────────────────────────────────────┘
```

### Vorteile dieses Ansatzes

| Eigenschaft | Wie erreicht |
|---|---|
| Audit-Trail | Change Feed |
| Replay | Events im Feed werden neu verarbeitet |
| Skalierbarkeit | Projections laufen unabhängig |
| DSGVO | Redaktion direkt im Feed möglich |
| Debugbarkeit | CRUD-State immer direkt lesbar |
| Einfachheit | Kein Framework-Magic |

## Was ist ein Change Feed?

Der Change Feed ist eine **append-only Tabelle** in PostgreSQL, die jeden signifikanten Zustandsübergang im System aufzeichnet. Er ist nicht der primäre Datenspeicher – das sind die normalen Tabellen. Aber er ist der **Synchronisationsbus** für alle abgeleiteten Systeme.

> Der Change Feed ist nicht primär Audit. Er ist der Realtime-Synchronisationsbus des Systems.

Jeder Eintrag im Feed enthält:

- Wer hat sich verändert? (`Entity`, `EntityId`)
- Was ist passiert? (`EventType`)
- Welche Daten? (`PayloadJson`)
- Welche Version des Schemas? (`Version`)
- Wann? (`Timestamp`)
- In welcher Reihenfolge? (`SequenceId`)

## Change Events vs Business Events

Nicht jedes fachliche Ereignis ist eine Zustandsaenderung.

- **Change Event**: entsteht aus einer persistierten Mutation (z.B. `UserEmailUpdated`)
- **Business Event**: fachliches Signal ohne zwingende Mutation (z.B. `UserLoggedIn`, `CheckoutViewed`)

Diese beiden Event-Typen sollten getrennt gespeichert und verarbeitet werden.

Warum?

- Change Events dienen Rebuild/Replay von Read Models
- Business Events dienen Prozesssteuerung, Analytics, Benachrichtigungen, Integrationen
- Login- oder Tracking-Events werden sonst kuenstlich als Datenaenderung modelliert

Empfohlene Regel:

> Alles, was fuer State-Rekonstruktion relevant ist, geht in `change_feed`. Alles andere in einen separaten `business_event_log`.

## Was sind Projections?

Projections sind asynchrone Hintergrundprozesse, die den Change Feed konsumieren und daraus Read Models, Suchindizes, Benachrichtigungen etc. aufbauen.

**Wichtigste Regel:**

> Projektionen dürfen NIE Teil des Write-Transactions sein.

Warum? Weil sonst:
- Schreibvorgänge langsam werden (alle Projections in der Transaktion)
- Ein Ausfall der Suchmaschine den Schreibpfad kaputt macht
- Cascading Failures entstehen

## Was ist ein Checkpoint?

Jede Projection speichert sich die zuletzt verarbeitete `SequenceId`. So kann jede Projection:

- unabhängig laufen
- bei Absturz weitermachen (nicht von vorne)
- bewusst von vorne starten (Replay)
- mit eigener Geschwindigkeit laufen

## Was ist Replay?

Replay bedeutet: Eine Projection setzt ihren Checkpoint zurück auf 0 und verarbeitet alle Events neu. Das ist kein Sonderfall – sondern ein normaler, geplanter Betriebsmodus.

Wann braucht man Replay?
- Projection-Code hat sich geändert
- Neues Read Model wird eingeführt
- Fehler in der Projection wurde behoben

## Was ist NICHT in diesem System

Bewusst **nicht** enthalten (und nicht notwendig):

- **Marten / EventStore**: Nicht nötig, PostgreSQL reicht
- **Kafka / RabbitMQ**: Polling über PostgreSQL reicht für die meisten Systeme
- **Upcasting-Pipeline**: Projections interpretieren Versionen selbst
- **Actor-System (Akka)**: Zu komplex ohne echten Mehrwert
- **Protobuf**: JSON ist flexibler für schema evolution

## Das mentale Modell in einem Satz

> Writes commit facts. Projections converge asynchronously.
