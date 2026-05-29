# 02 – Datenbank-Schema

## Überblick

Das System nutzt ausschließlich **PostgreSQL ≥ 14** (wegen `pg_current_snapshot()` und `xmin`-basiertem Sichtbarkeitsfilter, siehe [04-projections.md](04-projections.md)). Es gibt keine ORMs, keine Migration-Frameworks – nur direkte SQL-Skripte, die du einmal ausführst (oder in dein Migrations-Setup einbindest, z.B. mit [Flyway](https://flywaydb.org/) oder einfach als Init-Skripte).

> ⚠️ **Minimale PostgreSQL-Version: 14.** Ältere Versionen unterstützen `pg_current_snapshot()` nicht (dort hieß es `txid_current_snapshot()`). Der `xmin`-basierte Sichtbarkeitsfilter im Projection Worker setzt PG ≥ 13 voraus, PG ≥ 14 wird empfohlen.

## Tabellen

### `change_feed` – Das Herzstück

```sql
CREATE TABLE change_feed (
    sequence_id    BIGSERIAL   PRIMARY KEY,
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

CREATE INDEX idx_change_feed_sequence      ON change_feed (sequence_id);
CREATE INDEX idx_change_feed_entity_id     ON change_feed (entity, entity_id);
CREATE INDEX idx_change_feed_event_type    ON change_feed (event_type);
CREATE INDEX idx_change_feed_not_redacted  ON change_feed (sequence_id) WHERE redacted = FALSE;
CREATE INDEX idx_change_feed_correlation   ON change_feed (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX idx_change_feed_actor         ON change_feed (actor_id);
```

**Spalten erklärt:**

| Spalte | Typ | Bedeutung |
|---|---|---|
| `sequence_id` | `BIGSERIAL` | Monoton wachsende ID – die Reihenfolge der Events. **Kritisch für Checkpointing.** |
| `entity` | `TEXT` | Typ der betroffenen Entität, z.B. `"User"`, `"Asset"` |
| `entity_id` | `TEXT` | ID der Entität, z.B. die UUID des Users |
| `event_type` | `TEXT` | Name des Events, z.B. `"UserEmailUpdated"`, `"AssetCreated"` |
| `version` | `INT` | Schema-Version des Payloads. Beginnt bei 1. Wird erhöht, wenn sich die Payload-Struktur ändert. |
| `correlation_id` | `TEXT` | Optional. Verknüpft zusammengehörige Änderungen über mehrere Entitäten hinweg (z.B. eine fachliche Operation, die User + Asset ändert). Typischerweise eine Request-ID oder Prozess-ID. |
| `causation_id` | `TEXT` | Optional. Referenziert das Event, das dieses Event **direkt ausgelöst** hat. Ermöglicht die Rekonstruktion von Kausalketten (Event A → Event B → Event C). Siehe Abschnitt "Correlation vs. Causation" unten. |
| `actor_id` | `TEXT` | **Pflichtfeld.** Identifiziert den Akteur, der die Änderung ausgelöst hat – z.B. eine User-ID, `"system"`, `"migration"`, `"scheduler"`. Unverzichtbar für Audit-Trails und Nachvollziehbarkeit. Ohne `actor_id` ist Forensik bei Sicherheitsvorfällen unmöglich. |
| `payload` | `JSONB` | Nutzdaten des Events als JSON. JSONB ermöglicht indexierte Queries auf Felder. |
| `timestamp` | `TIMESTAMPTZ` | Zeitpunkt des Events, immer UTC. |
| `redacted` | `BOOLEAN` | DSGVO: wurde dieses Event auf Wunsch des Nutzers gelöscht/unkenntlich gemacht? |

**Indizes erklärt:**

| Index | Zweck |
|---|---|
| `idx_change_feed_sequence` | Effizientes Polling mit `WHERE sequence_id > @last_seen` |
| `idx_change_feed_entity_id` | Schnelle Abfrage aller Events einer Entität (z.B. für DSGVO-Auskunft) |
| `idx_change_feed_event_type` | Filtern nach Event-Typ (für Projections mit `EventTypes`-Filter) |
| `idx_change_feed_not_redacted` | **Partieller Index**: Nur nicht-redacted Events. Beschleunigt die Polling-Query der Projections erheblich, da redacted Events aus dem Index ausgeschlossen werden. |
| `idx_change_feed_correlation` | **Partieller Index**: Nur Events mit `correlation_id`. Ermöglicht schnelles Nachverfolgen zusammengehöriger Änderungen. |
| `idx_change_feed_actor` | Ermöglicht schnelle Abfrage aller Änderungen eines bestimmten Akteurs (z.B. "Zeige alle Änderungen von User X"). |

### Correlation vs. Causation

Diese beiden IDs werden oft verwechselt, haben aber unterschiedliche Aufgaben:

```
HTTP Request (correlation_id = "req-abc-123")
  │
  ├─ UserEmailUpdated  (causation_id = null,              correlation_id = "req-abc-123")
  │     │
  │     └─ NotificationSent (causation_id = "seq-42",     correlation_id = "req-abc-123")
  │           │
  │           └─ AuditLogWritten (causation_id = "seq-43", correlation_id = "req-abc-123")
  │
  └─ UserProfileUpdated (causation_id = null,             correlation_id = "req-abc-123")
```

- **`correlation_id`** = "Zu welchem übergeordneten Vorgang gehört dieses Event?" → Alle Events einer fachlichen Operation teilen dieselbe `correlation_id`. Typischerweise die Request-ID oder eine Prozess-ID.
- **`causation_id`** = "Welches Event hat dieses Event **direkt** ausgelöst?" → Bildet eine Kausalkette. Wenn Event B nur existiert, weil Event A verarbeitet wurde, dann ist `causation_id` von B die `sequence_id` von A.

**Wann braucht man `causation_id`?**
- Wenn Projections oder Sagas **Folge-Events** erzeugen (z.B. eine Notification-Projection schreibt ein `NotificationSent`-Event)
- Für Debugging: "Warum existiert dieses Event?" → Kausalkette rückwärts verfolgen
- Für Idempotenz: Prüfen ob ein Folge-Event bereits erzeugt wurde

**Wann reicht `correlation_id` allein?**
- Wenn Events nur durch HTTP-Requests entstehen (keine Event-getriebenen Folge-Events)
- In Phase 1–2 ist `correlation_id` oft ausreichend; `causation_id` wird relevant, sobald Projections oder Sagas selbst Events erzeugen

### Actor-ID: Wer hat die Änderung ausgelöst?

`actor_id` beantwortet die Frage: **Wer ist verantwortlich?**

| `actor_id` | Bedeutung |
|---|---|
| `"user:550e8400-..."` | Ein authentifizierter Benutzer |
| `"system"` | Automatischer Systemprozess |
| `"migration"` | Datenmigration |
| `"scheduler"` | Geplanter Job |
| `"admin:..."` | Admin-Eingriff |
| `"api-key:..."` | Externer API-Client |

**Warum nicht im Payload?**
- `actor_id` ist **Metadatum**, kein fachlicher Inhalt. Es gehört auf dieselbe Ebene wie `timestamp` und `correlation_id`.
- Ermöglicht systemweite Queries: "Zeige alle Änderungen von User X" – ohne jeden Payload parsen zu müssen.
- Unverzichtbar für Audit-Trails, Compliance und Forensik.

**Designentscheidung: Ein Feld mit Prefix statt zwei Felder (`actor_type` + `actor_id`)**

Die Alternative wäre eine Auftrennung in `actor_type TEXT` (z.B. `'user'`, `'system'`) und `actor_id TEXT` (z.B. die UUID). Bewusst gewählt wurde **ein Feld mit Prefix-Konvention**, weil:

- **Konsistenz mit dem Gesamtdesign:** Das System vermeidet bewusst Over-Engineering. Ein Feld reicht.
- **Keine Schema-Migration bei neuen Actor-Typen:** Ein neuer Typ wie `"webhook:..."` braucht keine Datenbankänderung.
- **Seltene Queries auf Actor-Typ:** In der Praxis fragt man meistens "Was hat User X gemacht?" (`WHERE actor_id = 'user:...'`), nicht "Wie viele System-Events gibt es?".
- **Einfache Konvention:** Format ist `type:id` oder nur `type` (bei Akteuren ohne eigene ID wie `"system"`).

Die Auftrennung in zwei Felder lohnt sich erst, wenn man **regelmäßig nach Actor-Typ filtern** muss (z.B. ein Dashboard "System vs. User-Änderungen") oder wenn man **FK-Constraints** auf die Users-Tabelle braucht. Beides ist in Phase 1–2 unwahrscheinlich.

### Warum `BIGSERIAL` und nicht UUID?

`BIGSERIAL` erzeugt eine monoton steigende Integer-Sequenz. Das ist für den Change Feed wichtig, weil:
- Polling mit `WHERE sequence_id > @last_seen` extrem effizient ist
- Reihenfolge garantiert ist (UUIDs haben keine natürliche Ordnung)
- Kein Sortierungs-Overhead entsteht

> ⚠️ **Wichtig: Sequence-Gaps bei parallelen Writes**
>
> PostgreSQL-Sequenzen (`BIGSERIAL`) sind **nicht transaktional**. Das bedeutet: Wenn zwei Transaktionen gleichzeitig in den Change Feed schreiben, kann Transaktion B (`sequence_id = 43`) vor Transaktion A (`sequence_id = 42`) committen. Ein Projection Worker, der zu diesem Zeitpunkt pollt, würde `43` sehen, aber `42` noch nicht – und könnte den Checkpoint auf `43` setzen, wodurch `42` **nie verarbeitet** wird.
>
> **Lösung:** Der `ProjectionWorker` verwendet einen `xmin`-basierten Sichtbarkeitsfilter (siehe [04-projections.md](04-projections.md)), der nur Events lädt, deren Transaktion für alle Sessions sichtbar ist. Damit werden uncommitted Events automatisch ausgeschlossen.

### Warum `entity_id` als `TEXT`?

`entity_id` ist bewusst als `TEXT` definiert, nicht als `UUID`. Das ermöglicht:
- Verschiedene ID-Formate pro Entity-Typ (UUID, Integer, Composite Keys)
- Keine Typ-Konvertierung beim Schreiben

**Trade-off:** Bei Systemen, die ausschließlich UUIDs verwenden, wäre `UUID` effizienter (16 Bytes vs. 36 Bytes als Text). Falls alle Entity-IDs UUIDs sind, kann `entity_id UUID NOT NULL` verwendet werden – die Entscheidung sollte bewusst getroffen und hier dokumentiert werden.

### Warum `JSONB` statt `TEXT`?

`JSONB` speichert JSON binär-indiziert. Das ermöglicht:
- Queries direkt auf JSON-Felder: `WHERE payload->>'email' = 'x@y.com'`
- GIN-Indizes für komplexe JSON-Queries
- Kein manuelles Parsen nötig bei Ad-hoc-Abfragen

---

### `projection_checkpoint` – Wo ist jede Projection?

```sql
CREATE TABLE projection_checkpoint (
    projection_name  TEXT   PRIMARY KEY,
    last_sequence_id BIGINT NOT NULL DEFAULT 0,
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

Jede Projection hat eine Zeile in dieser Tabelle. Der Wert `last_sequence_id` ist der letzte erfolgreich verarbeitete Eintrag im Change Feed.

**Beispielinhalt:**

| projection_name | last_sequence_id |
|---|---|
| user_read_model | 1420 |
| search_index | 1398 |
| analytics | 1100 |

---

### `projection_failures` – Fehler und Poison Events

Wenn eine Projection ein Event nicht verarbeiten kann, wird der Fehler hier erfasst. Damit bleibt der Fehlerpfad transparent und operativ bearbeitbar.

```sql
CREATE TABLE projection_failures (
    projection_name TEXT        NOT NULL,
    sequence_id     BIGINT      NOT NULL,
    event_type      TEXT        NOT NULL,
    attempts        INT         NOT NULL,
    last_error      TEXT        NOT NULL,
    next_retry_at   TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (projection_name, sequence_id)
);
```

---

### `business_event_log` – Fachliche Ereignisse ohne zwingende Mutation

Diese Tabelle speichert Business Events wie `UserLoggedIn`, `OrderPlaced`, `PaymentAuthorized`.

```sql
CREATE TABLE business_event_log (
    event_id       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type     TEXT        NOT NULL,
    entity         TEXT        NULL,
    entity_id      TEXT        NULL,
    actor_id       TEXT        NOT NULL,
    correlation_id TEXT        NULL,
    causation_id   TEXT        NULL,
    payload        JSONB       NOT NULL,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted       BOOLEAN     NOT NULL DEFAULT FALSE
);

CREATE INDEX idx_business_event_type        ON business_event_log (event_type);
CREATE INDEX idx_business_event_occurred_at ON business_event_log (occurred_at);
CREATE INDEX idx_business_event_entity      ON business_event_log (entity, entity_id);
CREATE INDEX idx_business_event_actor       ON business_event_log (actor_id);
```

**Änderungen gegenüber der Minimalversion:**

| Änderung | Begründung |
|---|---|
| `aggregate_id` → `entity` + `entity_id` | Konsistente Namensgebung mit `change_feed`. Ermöglicht übergreifende Queries. |
| `actor_id NOT NULL` | Pflichtfeld wie im `change_feed`. Jedes Event muss einem Akteur zugeordnet sein. |
| `redacted BOOLEAN` | DSGVO: Business Events können personenbezogene Daten enthalten (z.B. IP-Adresse in `UserLoggedIn`). Ohne `redacted`-Flag ist eine vollständige DSGVO-Löschung nicht möglich. |

`entity` und `entity_id` sind optional, weil manche Events keinen konkreten Entity-Bezug haben (z.B. `SystemHealthCheckFailed`).

---

### `event_outbox` – Zuverlaessige Weitergabe an externe Systeme

Falls Business Events an externe Systeme gehen (Webhook, Broker, Mailer), sollte eine Outbox verwendet werden.

```sql
CREATE TABLE event_outbox (
    outbox_id      BIGSERIAL   PRIMARY KEY,
    event_id       UUID        NOT NULL,
    event_type     TEXT        NOT NULL,
    payload        JSONB       NOT NULL,
    status         TEXT        NOT NULL DEFAULT 'Pending',
    attempts       INT         NOT NULL DEFAULT 0,
    next_retry_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error     TEXT        NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_event_outbox_status_retry ON event_outbox (status, next_retry_at);
```

---

### Domain-Tabellen (Beispiel: `users`)

Die eigentlichen Daten leben in normalen Tabellen. Das ist die CRUD Truth.

```sql
CREATE TABLE users (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    email      TEXT        NOT NULL,
    name       TEXT        NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

> Diese Tabellen sind der primäre, direkt lesbare Zustand. Projektionen sind *abgeleitet* – sie werden aus dem Change Feed aufgebaut.

---

## Komplettes Init-Skript

```sql
-- change_feed
CREATE TABLE change_feed (
    sequence_id    BIGSERIAL   PRIMARY KEY,
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

CREATE INDEX idx_change_feed_sequence      ON change_feed (sequence_id);
CREATE INDEX idx_change_feed_entity_id     ON change_feed (entity, entity_id);
CREATE INDEX idx_change_feed_event_type    ON change_feed (event_type);
CREATE INDEX idx_change_feed_not_redacted  ON change_feed (sequence_id) WHERE redacted = FALSE;
CREATE INDEX idx_change_feed_correlation   ON change_feed (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX idx_change_feed_actor         ON change_feed (actor_id);

-- projection_checkpoint
CREATE TABLE projection_checkpoint (
    projection_name  TEXT   PRIMARY KEY,
    last_sequence_id BIGINT NOT NULL DEFAULT 0,
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- projection_failures
CREATE TABLE projection_failures (
    projection_name TEXT        NOT NULL,
    sequence_id     BIGINT      NOT NULL,
    event_type      TEXT        NOT NULL,
    attempts        INT         NOT NULL,
    last_error      TEXT        NOT NULL,
    next_retry_at   TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (projection_name, sequence_id)
);

-- business_event_log
CREATE TABLE business_event_log (
    event_id       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type     TEXT        NOT NULL,
    entity         TEXT        NULL,
    entity_id      TEXT        NULL,
    actor_id       TEXT        NOT NULL,
    correlation_id TEXT        NULL,
    causation_id   TEXT        NULL,
    payload        JSONB       NOT NULL,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted       BOOLEAN     NOT NULL DEFAULT FALSE
);

CREATE INDEX idx_business_event_type        ON business_event_log (event_type);
CREATE INDEX idx_business_event_occurred_at ON business_event_log (occurred_at);
CREATE INDEX idx_business_event_entity      ON business_event_log (entity, entity_id);
CREATE INDEX idx_business_event_actor       ON business_event_log (actor_id);

-- event_outbox
CREATE TABLE event_outbox (
    outbox_id      BIGSERIAL   PRIMARY KEY,
    event_id       UUID        NOT NULL,
    event_type     TEXT        NOT NULL,
    payload        JSONB       NOT NULL,
    status         TEXT        NOT NULL DEFAULT 'Pending',
    attempts       INT         NOT NULL DEFAULT 0,
    next_retry_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error     TEXT        NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_event_outbox_status_retry ON event_outbox (status, next_retry_at);

-- domain: users (Beispiel)
CREATE TABLE users (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    email      TEXT        NOT NULL,
    name       TEXT        NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

## Warum kein ORM, kein Migration-Framework?

Für den Kernel bewusst nicht. Ein ORM abstrahiert genau das weg, was verstanden werden soll: wie Daten wirklich gespeichert werden. Dapper wird als dünner Mapping-Layer verwendet (optional), aber SQL bleibt immer sichtbar.
