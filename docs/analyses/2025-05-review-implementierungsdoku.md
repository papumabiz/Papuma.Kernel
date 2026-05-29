# Review: Implementierungsdokumentation `docs/implementation`

**Datum:** 2025-05-29  
**Reviewer:** Architektur-Review (automatisiert)  
**Scope:** Alle 8 Dokumente in `docs/implementation/`

---

## Gesamturteil

Die Dokumentation beschreibt eine pragmatische, gut begründete Architektur. Die Entscheidung für "CRUD Truth + Change Feed" statt purem Event Sourcing ist für die meisten Geschäftsanwendungen die richtige Wahl. Die Dokumentation ist klar strukturiert, ehrlich in ihren Trade-offs und vermeidet Over-Engineering.

**Bewertung nach Aspekt:**

| Aspekt | Bewertung | Kommentar |
|---|---|---|
| Grundidee (CRUD + Feed) | ⭐⭐⭐⭐⭐ | Pragmatisch, gut begründet |
| Datenbank-Schema | ⭐⭐⭐⭐ | Solide, kleine Lücken (Sequence-Gaps, fehlende Indizes) |
| Change Feed / Writer | ⭐⭐⭐⭐⭐ | Sauber, minimal, korrekt |
| Projection Worker | ⭐⭐⭐⭐ | Funktional, aber Checkpoint-Transaktion und Concurrency fehlen |
| Versionierung | ⭐⭐⭐⭐⭐ | Exzellenter Ansatz |
| DSGVO | ⭐⭐⭐⭐ | Guter Ansatz, Replay-nach-Redaktion sollte Pflicht sein |
| Projektstruktur | ⭐⭐⭐⭐⭐ | Klare Trennung, guter Phasenplan |
| Betriebsaspekte | ⭐⭐⭐ | Monitoring, Health-Checks, Alerting fehlen |

---

## Was besonders gut ist

### 1. Ehrliche Architekturentscheidungen
Die Begründungen in `README.md` und `01-konzepte.md` sind erfrischend ehrlich: *"Wenn du ein System nicht erklären kannst, solltest du es nicht betreiben."* Die Tabelle "Gewählt vs. Verworfen" ist exzellent – jede Entscheidung hat eine klare Begründung.

### 2. Atomare Write-Semantik
Die Kernidee – CRUD + Change Feed Append in **einer** Transaktion – ist der richtige Ansatz. Kein Dual-Write-Problem, keine Eventual Consistency auf der Write-Seite.

### 3. Trennung Change Events vs. Business Events
Die Unterscheidung ist architektonisch wertvoll. `UserLoggedIn` als Change Event zu modellieren wäre semantisch falsch. Die separate `business_event_log`-Tabelle verhindert diese Vermischung.

### 4. Versionierung ohne Upcasting
Projection-seitige Interpretation ist für die meisten Systeme die bessere Lösung als eine zentrale Upcasting-Pipeline.

### 5. Phasenplan
*"Erst nach 3 echten Projections abstrahieren. Nicht vorher. Sonst abstrahiert man Fantasie."* – genau die richtige Disziplin.

### 6. DSGVO-Lösung
Die Redaktionsstrategie ist die pragmatischste der drei Optionen. Kein Crypto-Overhead, transparent, auditierbar.

---

## Kritische Findings

### 🔴 K1: Checkpoint und Projection-Write sind nicht transaktional gekoppelt

**Betroffenes Dokument:** `04-projections.md`, `ProcessBatchAsync()`

**Problem:**
```
Schritt 1: _handler.HandleAsync(change, ct)     → Schreibt z.B. UPDATE users SET email = ...
Schritt 2: SaveCheckpointAsync(conn, ...)        → Schreibt UPDATE projection_checkpoint SET ...
```

Diese beiden Schritte verwenden **unterschiedliche Connections**. Der Handler öffnet seine eigene Connection, der Worker nutzt `conn` aus `ProcessBatchAsync()`.

**Crash-Szenario:**
1. Handler schreibt `UPDATE users SET email = 'neu@mail.com'` → **committed**
2. Applikation crasht **vor** `SaveCheckpointAsync`
3. Worker startet neu, liest denselben Checkpoint → **Event wird erneut verarbeitet**
4. Bei nicht-idempotenten Operationen: Zähler doppelt, E-Mails doppelt, API-Calls doppelt

**Lösung:** `IProjectionHandler` bekommt `NpgsqlConnection` + `NpgsqlTransaction` vom Worker. Handler-Write und Checkpoint-Update passieren in **einer** Transaktion. Details siehe Änderungen in `04-projections.md`.

**Einordnung:** Muss ab Phase 1 berücksichtigt werden.

---

### 🔴 K2: BIGSERIAL Sequence-Gaps – unsichtbare Events

**Betroffenes Dokument:** `02-datenbank.md`, `04-projections.md`

**Problem:** PostgreSQL `BIGSERIAL` basiert auf `CREATE SEQUENCE`, und Sequenzen sind **nicht transaktional**:

```
T1: Transaktion A holt sequence_id = 42, beginnt INSERT
T2: Transaktion B holt sequence_id = 43, INSERT + COMMIT → sichtbar
T3: Projection Worker pollt: WHERE sequence_id > 41 → findet 43, NICHT 42
T4: Worker setzt Checkpoint auf 43
T5: Transaktion A committed → sequence_id 42 ist jetzt sichtbar
T6: Worker pollt: WHERE sequence_id > 43 → 42 wird NIE gefunden
```

**Event 42 ist für immer verloren** für diese Projection.

**Lösung:** `xmin`-basierter Sichtbarkeitsfilter in der Polling-Query. PostgreSQL's `pg_current_snapshot()` garantiert, dass nur Rows geladen werden, deren Transaktion für alle sichtbar ist. Details siehe Änderungen in `04-projections.md`.

**Einordnung:** Muss ab Phase 1 berücksichtigt werden.

---

### 🔴 K3: Race Condition bei Replay

**Betroffenes Dokument:** `04-projections.md`, `ReplayService`

**Problem:** `ResetCheckpointAsync()` setzt den Checkpoint auf 0, aber der Worker läuft gleichzeitig weiter:

```
T1: Worker verarbeitet Event 1500, Checkpoint = 1500
T2: Admin ruft ResetCheckpointAsync("user_read_model") → Checkpoint = 0
T3: Worker verarbeitet Event 1501, SaveCheckpointAsync → Checkpoint = 1501
```

Der Reset wurde sofort überschrieben. Der Replay hat nie stattgefunden.

**Lösung:** Replay-Request geht über einen `Channel<ReplayRequest>` an den Worker. Der Worker prüft am Anfang jedes Loops, ob ein Replay angefordert wurde. Da der Worker single-threaded ist, gibt es keine Race Condition. Details siehe Änderungen in `04-projections.md`.

**Einordnung:** Muss ab Phase 2 berücksichtigt werden (sobald Replay operativ genutzt wird).

---

## Wichtige Findings

### 🟡 W1: Problematische Subquery in `LoadChangesAsync`

**Betroffenes Dokument:** `04-projections.md`

Die korrelierte Subquery gegen `projection_failures` wird bei großem Change Feed und vielen Failures langsam. Die Semantik ist zudem schwer zu durchschauen.

**Empfehlung:** Failure-Prüfung in den Application-Code verlagern (nach dem Laden) oder klareren Kommentar hinzufügen.

---

### 🟡 W2: Singleton-Registrierung und Connection-Pool

**Betroffenes Dokument:** `04-projections.md`

Der Worker öffnet pro Batch eine Connection, der Handler öffnet eine eigene. Bei vielen Projections und hoher Last: **zwei offene Connections pro Event pro Projection**.

**Empfehlung:** Connection-Pool auf mindestens `2 × Anzahl Projections + Headroom` dimensionieren. Besser: Connection vom Worker an Handler durchreichen (wird durch K1-Fix automatisch gelöst).

---

### 🟡 W3: Retry-Intervall ist fix

**Betroffenes Dokument:** `04-projections.md`

Das Retry-Intervall ist immer `5 seconds`. Exponentielles Backoff wäre robuster bei transienten Fehlern.

**Empfehlung:** Backoff-Formel: `MIN(base_delay * 2^(attempts-1), max_delay)`, z.B. 5s → 10s → 20s → 40s → 60s (cap).

---

### 🟡 W4: DSGVO-Redaktion und laufende Projections

**Betroffenes Dokument:** `06-gdpr.md`

Wenn eine Projection gerade Events für einen User verarbeitet, hat sie den **originalen Payload** bereits im Speicher. Die Redaktion im Feed hat keinen Effekt auf bereits geladene Events.

**Empfehlung:** Nach einer Redaktion ist ein Replay der betroffenen Projections **Pflicht**, nicht optional. Die Read-Model-Bereinigung muss als verbindlicher Schritt dokumentiert werden.

---

## Kleinere Findings

### 🟢 F1: Fehlender partieller Index auf `redacted`

Die Query `WHERE redacted = FALSE` profitiert von:
```sql
CREATE INDEX idx_change_feed_not_redacted ON change_feed (sequence_id) WHERE redacted = FALSE;
```

### 🟢 F2: `entity_id` als `TEXT` statt `UUID`

Flexibel, aber bei UUID-basierten Systemen: Verlust von Typ-Sicherheit und Storage-Effizienz (UUID = 16 Bytes, TEXT-UUID = 36 Bytes). Bewusste Entscheidung dokumentieren.

### 🟢 F3: Kein `correlation_id` im Change Feed

Der `business_event_log` hat `correlation_id` und `causation_id`, der `change_feed` nicht. Für Debugging und Tracing wäre eine `correlation_id` im Change Feed hilfreich, um zusammengehörige Änderungen über mehrere Entitäten nachzuverfolgen.

### 🟢 F4: Kein Health-Check / Monitoring

Es fehlt eine Beschreibung, wie man den Zustand der Projections überwacht. Ein Health-Check-Endpoint, der die Differenz zwischen `MAX(sequence_id)` im Feed und den Checkpoints zeigt, wäre operativ sehr wertvoll.

---

## Umsetzung

Alle Findings wurden in die jeweiligen Implementierungsdokumente eingearbeitet:

| Finding | Eingearbeitet in |
|---|---|
| K1: Transaktionale Checkpoint-Kopplung | `04-projections.md` |
| K2: Sequence-Gaps / xmin-Filter | `02-datenbank.md`, `04-projections.md` |
| K3: Replay Race Condition | `04-projections.md` |
| W1: Subquery-Performance | `04-projections.md` |
| W2: Connection-Pool | `04-projections.md` |
| W3: Exponentielles Backoff | `04-projections.md` |
| W4: Redaktion + Replay | `06-gdpr.md` |
| F1: Partieller Index | `02-datenbank.md` |
| F2: entity_id TEXT vs UUID | `02-datenbank.md` |
| F3: correlation_id im Feed | `02-datenbank.md`, `03-change-feed.md` |
| F4: Monitoring | `07-projektstruktur.md` |
