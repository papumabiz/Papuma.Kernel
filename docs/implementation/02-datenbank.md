# 02 – Datenbank-Schema

## Überblick

Das System nutzt ausschließlich PostgreSQL. Es gibt keine ORMs, keine Migration-Frameworks – nur direkte SQL-Skripte, die du einmal ausführst (oder in dein Migrations-Setup einbindest, z.B. mit [Flyway](https://flywaydb.org/) oder einfach als Init-Skripte).

## Tabellen

### `change_feed` – Das Herzstück

```sql
CREATE TABLE change_feed (
    sequence_id   BIGSERIAL PRIMARY KEY,
    entity        TEXT        NOT NULL,
    entity_id     TEXT        NOT NULL,
    event_type    TEXT        NOT NULL,
    version       INT         NOT NULL DEFAULT 1,
    payload       JSONB       NOT NULL,
    timestamp     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted      BOOLEAN     NOT NULL DEFAULT FALSE
);

CREATE INDEX idx_change_feed_sequence   ON change_feed (sequence_id);
CREATE INDEX idx_change_feed_entity_id  ON change_feed (entity, entity_id);
CREATE INDEX idx_change_feed_event_type ON change_feed (event_type);
```

**Spalten erklärt:**

| Spalte | Typ | Bedeutung |
|---|---|---|
| `sequence_id` | `BIGSERIAL` | Monoton wachsende ID – die Reihenfolge der Events. **Kritisch für Checkpointing.** |
| `entity` | `TEXT` | Typ der betroffenen Entität, z.B. `"User"`, `"Asset"` |
| `entity_id` | `TEXT` | ID der Entität, z.B. die UUID des Users |
| `event_type` | `TEXT` | Name des Events, z.B. `"UserEmailUpdated"`, `"AssetCreated"` |
| `version` | `INT` | Schema-Version des Payloads. Beginnt bei 1. Wird erhöht, wenn sich die Payload-Struktur ändert. |
| `payload` | `JSONB` | Nutzdaten des Events als JSON. JSONB ermöglicht indexierte Queries auf Felder. |
| `timestamp` | `TIMESTAMPTZ` | Zeitpunkt des Events, immer UTC. |
| `redacted` | `BOOLEAN` | DSGVO: wurde dieses Event auf Wunsch des Nutzers gelöscht/unkenntlich gemacht? |

### Warum `BIGSERIAL` und nicht UUID?

`BIGSERIAL` erzeugt eine monoton steigende Integer-Sequenz. Das ist für den Change Feed wichtig, weil:
- Polling mit `WHERE sequence_id > @last_seen` extrem effizient ist
- Reihenfolge garantiert ist (UUIDs haben keine natürliche Ordnung)
- Kein Sortierungs-Overhead entsteht

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
    event_id      UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type    TEXT        NOT NULL,
    aggregate_id  TEXT        NULL,
    correlation_id TEXT       NULL,
    causation_id   TEXT       NULL,
    payload       JSONB       NOT NULL,
    occurred_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_business_event_type        ON business_event_log (event_type);
CREATE INDEX idx_business_event_occurred_at ON business_event_log (occurred_at);
CREATE INDEX idx_business_event_aggregate   ON business_event_log (aggregate_id);
```

`aggregate_id` ist optional, weil manche Events keinen konkreten Aggregate-State betreffen.

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
    sequence_id   BIGSERIAL   PRIMARY KEY,
    entity        TEXT        NOT NULL,
    entity_id     TEXT        NOT NULL,
    event_type    TEXT        NOT NULL,
    version       INT         NOT NULL DEFAULT 1,
    payload       JSONB       NOT NULL,
    timestamp     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted      BOOLEAN     NOT NULL DEFAULT FALSE
);

CREATE INDEX idx_change_feed_sequence   ON change_feed (sequence_id);
CREATE INDEX idx_change_feed_entity_id  ON change_feed (entity, entity_id);
CREATE INDEX idx_change_feed_event_type ON change_feed (event_type);

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
    event_id      UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type    TEXT        NOT NULL,
    aggregate_id  TEXT        NULL,
    correlation_id TEXT       NULL,
    causation_id   TEXT       NULL,
    payload       JSONB       NOT NULL,
    occurred_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_business_event_type        ON business_event_log (event_type);
CREATE INDEX idx_business_event_occurred_at ON business_event_log (occurred_at);
CREATE INDEX idx_business_event_aggregate   ON business_event_log (aggregate_id);

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
