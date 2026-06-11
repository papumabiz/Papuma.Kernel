# Papuma vNEXT — Grobarchitektur

Status: Entwurf (2026-06-11) · Umsetzung: [implementation-plan.md](implementation-plan.md) ·
Hintergründe: [concepts.md](concepts.md) (das "Warum hinter dem Wie", erzählend)

Dieses Dokument beschreibt den Reboot von Papuma.Kernel als **Document-Sourced CQRS**:
JSON-Dokumente sind die Wahrheit, der Change Feed entsteht automatisch als Diff,
Projektionen sind bewusst dumme Change Handler. Es gibt keinen Migrationspfad von v1 —
vNEXT ist ein Neuanfang (siehe [chat-1.md](chat-1.md) für die Herleitung).

Die verbindlichen Einzelentscheidungen stehen in [adr/](adr/) — dieses Dokument ist die
Landkarte darüber.

---

## 1. Problemstellung

Das eigentliche Problem von Papuma war nie "Wie speichere ich Daten?", sondern:

> Wie bekomme ich Änderungen als First-Class-Konzept, ohne Event-Sourcing-Zwang?

v1 hat das mit relationalen Tabellen, Outbox und explizit erzeugten Events gelöst —
mit dem bekannten Preis: Change Detection ist mühsam, Events müssen manuell definiert
werden, DSGVO-relevante Daten landen in unveränderlichen Feeds.

vNEXT dreht das Modell um:

```text
Entity (C#-Klasse)
      ↓
JSON-Dokument (Wahrheit)
      ↓
Change Kernel (Diff, Version, Policies)
      ↓
PostgreSQL (Dokument + Change Feed, eine Transaktion)
      ↓
Change Handler (Projektionen, Suche, Audit, Integration)
```

Abgrenzung zu Marten: Dort ist das **Event** die Wahrheit und der State abgeleitet.
Bei Papuma ist das **Dokument** die Wahrheit und der Change Stream abgeleitet.
Konzeptionell näher an "Git für Aggregate" bzw. dem Cosmos-DB-Change-Feed als an
Event Sourcing.

---

## 2. Leitprinzipien

1. **Dokument ist Wahrheit.** Der aktuelle Zustand liegt als JSONB-Dokument vor; der
   Change Feed ist abgeleitet, nicht umgekehrt ([ADR-002](adr/adr-002-document-as-truth.md)).
2. **PostgreSQL ≥ 18, ohne Provider-Abstraktion.** Der Kernel nutzt JSONB,
   Expression-Indizes, LISTEN/NOTIFY und `RETURNING OLD/NEW` bewusst aus
   ([ADR-001](adr/adr-001-postgresql-18-only.md)).
3. **Ein Write = eine Transaktion = ein atomarer Roundtrip.** Optimistische Concurrency
   über eine `version`-Spalte ist eine Invariante des Kernels, kein Implementierungsdetail
   ([ADR-003](adr/adr-003-write-path-concurrency.md)).
4. **Der Change Feed speichert Diffs, keine Snapshots.** Reversibel, schlank,
   policy-fähig ([ADR-004](adr/adr-004-changerecord-diff-only.md)).
5. **Schema-Evolution ist ein Tag-1-Konzept.** Jedes Dokument trägt eine
   `schema_version`; Upcaster heben alte Dokumente beim Laden an
   ([ADR-005](adr/adr-005-schema-evolution.md)).
6. **Constraints kommen kontrolliert zurück.** Uniqueness und Lookup-Keys werden im
   Metamodell deklariert und als JSONB-Expression-Indizes materialisiert — nicht ad-hoc
   ([ADR-006](adr/adr-006-keys-and-constraints.md)).
7. **Datenschutz ist Kernel-Aufgabe.** Policies (Redact, Reference, DoNotTrack, Hash)
   werden beim Erzeugen des Diffs angewendet, bevor irgendetwas den Feed erreicht
   ([ADR-007](adr/adr-007-privacy-policies.md)).
8. **Keine fachlichen Events im Storage Layer.** Der Kernel kennt nur `DocumentChanged`;
   `OrderPaid` entsteht — wenn überhaupt — in der Processing-Schicht
   ([ADR-011](adr/adr-011-no-business-events-in-storage.md)).
9. **Projektionen sind dumm, die Engine ist Infrastruktur.** Kein SQL-Generator, keine
   Read-Model-DSL ([ADR-009](adr/adr-009-projections-as-dumb-handlers.md)).

---

## 3. Schichten und Projektstruktur

vNEXT bleibt eine kleine Library (ein Paket plus optionale ASP.NET-Core-Integration),
die Schichten sind Namespaces, keine eigenen NuGet-Pakete:

```text
Papuma.Kernel
├── Papuma.Kernel.Store        Dokumente laden/speichern/löschen, Write-Pfad
├── Papuma.Kernel.Changes      ChangeRecord, Diff-Engine, Policies, Feed-Tabellen
├── Papuma.Kernel.Processing   Change-Handler-Engine: Checkpoints, Retry, Rebuild
└── Papuma.Kernel.Model        Metamodell: Typen, Keys, Policies, Schema-Versionen

Papuma.Kernel.AspNetCore       Tenant-Resolution, Hosting der Processing-Worker
```

Die Aufteilung in `Store / Changes / Processing` folgt der Skizze aus chat-1.md
("Papuma.Store / Papuma.ChangeFeed / Papuma.Processing"), aber ohne Paket-Splitting —
das wäre verfrühte Abstraktion.

---

## 4. Datenmodell (PostgreSQL)

```sql
CREATE TABLE papuma.document
(
    scope           text        NOT NULL,   -- 'Platform' | 'Tenant' (Scope-Modell aus v1)
    tenant_id       text        NOT NULL DEFAULT '',  -- leer bei Platform-Scope
    document_type   text        NOT NULL,   -- logischer Aggregatname aus dem Metamodell
    id              text        NOT NULL,
    version         bigint      NOT NULL,   -- optimistische Concurrency, startet bei 1
    schema_version  int         NOT NULL,   -- Stand der C#-Klasse beim letzten Schreiben
    data            jsonb       NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (scope, tenant_id, document_type, id)
);

CREATE TABLE papuma.change
(
    seq             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope           text        NOT NULL,
    tenant_id       text        NOT NULL DEFAULT '',
    document_type   text        NOT NULL,
    document_id     text        NOT NULL,
    version         bigint      NOT NULL,   -- Dokumentversion NACH der Änderung
    schema_version  int         NOT NULL,
    operation       smallint    NOT NULL,   -- 1=Insert, 2=Update, 3=Delete
    diff            jsonb       NOT NULL,   -- siehe ADR-004 (reversibles Feld-Diff)
    metadata        jsonb       NOT NULL,   -- IsRollback, RestoredVersion, Actor, CorrelationId, ...
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    txid            xid8        NOT NULL DEFAULT pg_current_xact_id()  -- für lückenloses Lesen, ADR-010
);

CREATE UNIQUE INDEX ux_change_document_version
    ON papuma.change (scope, tenant_id, document_type, document_id, version);
```

Beide Tabellen tragen Row-Level-Security-Policies (inkl. `'All'`-Scope für Worker und
`FORCE ROW LEVEL SECURITY`); die verbindliche DDL liegt in `SchemaDdl.cs`.

Unique-Keys und Lookup-Spalten pro Dokumenttyp entstehen als Expression-Indizes aus dem
Metamodell, z. B.:

```sql
CREATE UNIQUE INDEX ux_user_email
    ON papuma.document (tenant_id, (data ->> 'email'))
    WHERE document_type = 'User';
```

---

## 5. Der Write-Pfad

Kern des Designs: **ein einziges atomares Statement liefert alten und neuen Zustand**,
dank PostgreSQL ≥ 18 `RETURNING OLD/NEW`. Kein vorheriges Laden, kein zweiter Roundtrip,
kein Fenster für Race Conditions.

```sql
-- Update mit optimistischer Concurrency
UPDATE papuma.document
SET data           = @data,
    version        = version + 1,
    schema_version = @schemaVersion,
    updated_at     = now()
WHERE tenant_id = @tenantId
  AND document_type = @type
  AND id = @id
  AND version = @expectedVersion
RETURNING old.data AS old_data, new.data AS new_data, new.version;
```

- **0 Zeilen** → `ConcurrencyException` (jemand anderes hat zwischenzeitlich geschrieben
  oder das Dokument existiert nicht).
- **1 Zeile** → der Kernel diffed `old_data` gegen `new_data` in C#, wendet die
  Datenschutz-Policies an und schreibt den `ChangeRecord` **in derselben Transaktion**.

Insert (`old` ist NULL) und Delete (`new` ist NULL, `DELETE ... RETURNING old.data`)
folgen demselben Muster. Details und API-Skizze: [ADR-003](adr/adr-003-write-path-concurrency.md).

Die öffentliche API bleibt klein:

```csharp
DocumentSession session = store.OpenSession(tenant);

SaveResult<User> result = await session.SaveAsync(user, expectedVersion);
// result.Version, result.Operation, result.Diff (policy-bereinigt)

await session.DeleteAsync<User>(id, expectedVersion);
User? current = await session.LoadAsync<User>(id);          // inkl. Upcasting
```

### Partielle Updates (Patch)

Für Einzelfeld-Änderungen gibt es ein zweites Write-Primitiv — **ohne vorheriges
Laden**, weder durch den Aufrufer noch intern:

```csharp
await session.PatchAsync<User>(id, p => p.Set(x => x.DisplayName, "Harry"));
```

Der "Read" passiert im `UPDATE` selbst (`jsonb_set` + `RETURNING old.data, new.data`);
Diff, Policies und ChangeRecord entstehen wie beim Save. Concurrency ist beim Patch
opt-in (Field-Level Last-Writer-Wins ohne `expectedVersion`). Details und Grenzen des
Operationskatalogs: [ADR-012](adr/adr-012-partial-updates.md).

### Session = Unit of Work

Eine `DocumentSession` bündelt mehrere Writes in **einer** Postgres-Transaktion:

```csharp
session.Save(user, expectedVersion: 0);      // Registrierung: zwei Aggregate,
session.Save(address, expectedVersion: 0);   // ein atomarer Commit
await session.CommitAsync();
```

Dokumente und ChangeRecords aller Writes werden atomar sichtbar. Konsumenten sehen
pro Dokument einen eigenen ChangeRecord; eine gemeinsame `correlationId` in den
Change-Metadaten verbindet die Writes einer Session fachlich. Modellierungs-Faustregel
bleibt davon unberührt: *Embed by default* — was zusammen konsistent sein muss, gehört
in **ein** Dokument; eigene Aggregate nur bei eigenem Lebenszyklus.

Rollback ist bewusst **kein eigener Operationstyp**, sondern ein Update mit
`metadata.IsRollback = true` ([ADR-008](adr/adr-008-rollback-is-update.md)).

---

## 6. Metamodell

Beim Start (zunächst Reflection, später optional Source Generator) baut der Kernel pro
registriertem Dokumenttyp ein vollständiges Metamodell:

> **Source Generator — wann, nicht ob:** Der Reflection-Scan läuft einmal beim Start
> (Millisekunden); Laufzeit-Performance ist kein SG-Argument. Die Fluent-Overrides
> bleiben per ADR-007 ohnehin Runtime (Policies ohne Recompile änderbar) — ein SG kann
> nur den Attribut-Teil vorberechnen. Trigger für den Umstieg: (a) NativeAOT/Trimming
> als Ziel (dann zusammen mit STJ-`JsonSerializerContext`), (b) Compile-Zeit-Diagnostik
> als DX-Politur. Da `KernelModel` eine immutable Datenstruktur ist, ist der Tausch der
> Bauquelle für alle Konsumenten unsichtbar — die Entscheidung ist gefahrlos vertagt.

```csharp
DocumentTypeMetadata
{
    Name           = "User",
    ClrType        = typeof(User),
    SchemaVersion  = 3,                    // höchster registrierter Upcaster + 1
    Keys           = [ UniqueKey("email") ],
    Properties     =
    [
        { Path = "email",     Policy = Reference },
        { Path = "phone",     Policy = Redact },
        { Path = "lastSeen",  Policy = DoNotTrack },
        { Path = "name",      Policy = Track }      // Default
    ]
}
```

Quellen des Metamodells, in dieser Prioritätsreihenfolge:

1. **Fluent-Konfiguration** beim Store-Setup (organisationsspezifische Overrides),
2. **Attribute** an der C#-Klasse (`[SensitiveData]`, `[DoNotTrack]`, `[TrackHash]`,
   `[UniqueKey]`) als Default am Ort der Wahrheit,
3. Konvention (alles wird getrackt).

Begründung und Attribut-/Policy-Katalog: [ADR-007](adr/adr-007-privacy-policies.md),
Keys: [ADR-006](adr/adr-006-keys-and-constraints.md).

---

## 7. Schema-Evolution

Die C#-Klasse ist die Wahrheit von *heute* — in der Datenbank liegen Dokumente von
*gestern*. Deshalb ([ADR-005](adr/adr-005-schema-evolution.md)):

- Jedes Dokument und jeder ChangeRecord trägt `schema_version`.
- Pro Typ werden Upcaster registriert, die JSON von Version n nach n+1 heben:

  ```csharp
  builder.For<User>()
      .Upcast(fromVersion: 1, json => { json["email"] = json["mail"]; json.Remove("mail"); });
  ```

- Upcasting passiert **beim Laden** (lazy); das Dokument wird erst beim nächsten
  `Save` physisch auf den neuen Stand geschrieben.
- ChangeRecords werden **nie** rückwirkend migriert — Konsumenten alter Changes müssen
  mit der damaligen `schema_version` umgehen (oder den Replay über Upcaster laufen lassen).

---

## 8. Change-Konsum und Projektionen

### Handler-Modell

```csharp
public interface IChangeHandler
{
    string Name { get; }
    Task HandleAsync(ChangeRecord change, CancellationToken ct);
}
```

Mehr nicht. Ein Handler kann eine SQL-Projektion sein, ein Suchindex-Update, ein
Webhook, ein Audit-Log, ein Event-Translator. Der Kernel generiert kein SQL und kennt
keine Read Models ([ADR-009](adr/adr-009-projections-as-dumb-handlers.md)).

Die Engine liefert die Infrastruktur:

- **Reihenfolge**: pro Handler strikt nach `seq`; pro Dokument damit automatisch nach `version`.
- **Checkpoints**: pro Handler eine persistierte Position (`papuma.checkpoint`).
- **Retry** mit Backoff und Poison-Handling.
- **Rebuild**: Checkpoint auf 0, Feed-Replay (Upcaster optional dazwischengeschaltet).

### Lückenloses Lesen

Ein naives `WHERE seq > @lastSeq` verliert Änderungen, deren Transaktion später committet
als eine mit höherer `seq`. Deshalb liest die Engine snapshot-basiert: nur Changes, deren
`txid` vor `pg_snapshot_xmin(pg_current_snapshot())` liegt, gelten als sichtbar-stabil.
LISTEN/NOTIFY dient nur als Wakeup, Polling bleibt die Wahrheit
([ADR-010](adr/adr-010-feed-consumption.md), aufbauend auf
[polling-vs-listen-analysis.md](../analyses/polling-vs-listen-analysis.md)).

### Komfort obendrauf, nicht darunter

Helfer wie

```csharp
WhenFieldChanged<User>(x => x.Email)
```

sind dünne Filter über `ChangeRecord.Diff` — Zucker über dem Handler-Interface, keine
eigene Abstraktionsschicht.

### Fachliche Events: drei Fälle

| Fall | Beispiel | Modellierung |
|------|----------|--------------|
| Zustandsübergang | `OrderPlaced`, `OrderPaid` | Translator-Handler leitet aus dem Diff ab ([ADR-011](adr/adr-011-no-business-events-in-storage.md)) |
| Faktum ohne Zustand | `UserLoggedIn`, `EmailSent` | `session.Append(...)` ins append-only **Event-Log** ([ADR-013](adr/adr-013-business-event-log.md)) |
| Trigger ("danach X auslösen") | Bestätigungsmail | Handler-Subscription — kein gespeichertes Event |

Das Event-Log (`papuma.event`) teilt Session-Transaktion, Policies, Metamodell und
Processing-Engine mit dem Change Feed, hat aber eigene Checkpoints und erlaubt
Typ-spezifische Retention. Rote Linie: Es ist **niemals Replay-Quelle für Zustand**.

---

## 9. Datenschutz-Schicht

Policies werden beim Erzeugen des Diffs angewendet — **bevor** der ChangeRecord
geschrieben wird:

| Policy        | Diff-Eintrag                                  | Verwendung                          |
|---------------|-----------------------------------------------|-------------------------------------|
| `Track`       | `{ "old": ..., "new": ... }`                  | Default                             |
| `Redact`      | `{ "changed": true }`                         | PII, die niemand im Feed braucht    |
| `Reference`   | `{ "ref": "User/123/email" }`                 | Wert bleibt ausschließlich im Dokument bzw. Sensitive Store |
| `Hash`        | `{ "changed": true, "hash": "..." }`          | Passwort-Hashes, Vergleichbarkeit ohne Inhalt |
| `DoNotTrack`  | Feld erscheint nicht im Diff                  | Telemetrie-Felder                   |

Wird ein Dokument DSGVO-gelöscht, verschwinden die Werte mit dem Dokument; der Feed
enthält nur noch Referenzen und `changed`-Flags — keine personenbezogenen Inhalte.
Das Hybrid-Modell aus [adr-2026-06-sensitive-data-reference-pattern.md](../analyses/adr-2026-06-sensitive-data-reference-pattern.md)
(expliziter, versionierter Sensitive Data Store, opt-in-Auflösung in Projektionen) wird
konzeptionell übernommen ([ADR-007](adr/adr-007-privacy-policies.md)).

---

## 10. Tenancy

Mehrmandantenfähigkeit bleibt First-Class wie in v1: `tenant_id` ist Teil des
Primärschlüssels von Dokumenten und Changes, die `DocumentSession` ist immer an einen
Tenant gebunden, und `Papuma.Kernel.AspNetCore` liefert weiterhin die Tenant-Resolution.

---

## 11. Bewusst NICHT Teil von vNEXT

- **Provider-Abstraktion / andere Datenbanken** — Postgres-only, siehe ADR-001.
- **Event Sourcing / Event Store** — Changes sind abgeleitet, nicht die Wahrheit.
- **Read-Model-Generierung, Query-DSL, LINQ-Provider** — Projektionen schreiben SQL selbst.
- **Cross-Document-Transaktionen über die Session hinaus** — innerhalb einer Session
  sind Multi-Dokument-Commits atomar (Abschnitt 5, "Session = Unit of Work");
  Cross-Document-*Constraints* und verteilte Sagas sind dagegen Anwendungssache.
- **Automatische fachliche Events** — siehe ADR-011.
- **Migrationscode von v1** — der Reboot ist vollständig.

---

## 12. ADR-Index

| ADR | Titel | Status |
|-----|-------|--------|
| [001](adr/adr-001-postgresql-18-only.md) | PostgreSQL ≥ 18 als einzige Zieldatenbank | Accepted |
| [002](adr/adr-002-document-as-truth.md) | Dokument als Source of Truth, Change Feed abgeleitet | Accepted |
| [003](adr/adr-003-write-path-concurrency.md) | Atomarer Write-Pfad mit optimistischer Concurrency und RETURNING OLD/NEW | Accepted |
| [004](adr/adr-004-changerecord-diff-only.md) | ChangeRecord speichert reversibles Diff, keine Snapshots | Accepted |
| [005](adr/adr-005-schema-evolution.md) | Schema-Evolution über schema_version und Upcaster | Accepted |
| [006](adr/adr-006-keys-and-constraints.md) | Keys und Constraints über Metamodell und Expression-Indizes | Accepted |
| [007](adr/adr-007-privacy-policies.md) | Datenschutz-Policies: Attribute als Default, Fluent als Override | Accepted |
| [008](adr/adr-008-rollback-is-update.md) | Rollback ist ein Update mit Metadata | Accepted |
| [009](adr/adr-009-projections-as-dumb-handlers.md) | Projektionen als dumme Change Handler | Accepted |
| [010](adr/adr-010-feed-consumption.md) | Snapshot-basiertes Polling mit LISTEN/NOTIFY-Wakeup | Accepted |
| [011](adr/adr-011-no-business-events-in-storage.md) | Keine fachlichen Events im Storage Layer | Accepted |
| [012](adr/adr-012-partial-updates.md) | Partielle Updates als Patch-Primitiv (ohne Load, atomar via jsonb_set + RETURNING) | Accepted |
| [013](adr/adr-013-business-event-log.md) | Fachliche Events: Translator, append-only Event-Log und Trigger-Handler | Accepted |
| [014](adr/adr-014-bulk-operations.md) | Bulk-Operationen als set-basierter Patch (Key-Prädikate oder ID-Listen, ein ChangeRecord pro Dokument) | Accepted |

## 13. Rezepte (Tutorial-Vorstufen)

Anwendungsmuster auf Basis der ADRs, als Entwürfe für spätere Tutorials:

- [Echtzeit-UI-Benachrichtigungen über Dokumentänderungen](recipes/realtime-ui-notifications.md)
  — SignalR-Notifier als Change Handler, inkl. Abgrenzung zu Presence.
