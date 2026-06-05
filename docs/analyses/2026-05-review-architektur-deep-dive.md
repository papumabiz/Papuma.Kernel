# Deep-Dive Architektur-Review: Implementierungsdokumentation

**Datum:** 2026-05-29<br />
**Reviewer:** Software-Architekt (manuell)<br />
**Scope:** Alle 9 Dokumente in `docs/implementation/` – Post-Review-Stand (nach Einarbeitung der Findings aus 2025-05)<br />
**Kontext:** Review als erfahrener Software-Architekt mit Fokus auf Event Sourcing, CRUD-Systeme, Betriebssicherheit und Zukunftsfähigkeit<br />
**Status:** ✅ Alle kritischen und wichtigen Findings wurden in die Implementierungsdokumentation eingearbeitet.

---

## Executive Summary

Die Architektur ist **überdurchschnittlich gut durchdacht** für ein System dieser Größenordnung. Der "CRUD Truth + Change Feed"-Ansatz ist eine pragmatische und tragfähige Entscheidung, die die häufigsten Fehler von Event-Sourcing-Projekten vermeidet. Die Dokumentation ist ehrlich, gut strukturiert und vermeidet bewusst Over-Engineering.

Nach Einarbeitung der Findings aus dem ersten Review (K1–K3, W1–W4, F1–F4) sind die kritischsten Probleme adressiert. Dieser Review fokussiert auf **verbleibende Architekturrisiken, Sicherheitsaspekte, Betriebsreife und Zukunftsfähigkeit**.

**Gesamtbewertung: 8/10** – Produktionsreif mit den unten genannten Einschränkungen.

---

## 1. Architekturentscheidungen – Was exzellent ist

### 1.1 CRUD Truth als primäre Datenquelle
Die Entscheidung, CRUD-Tabellen als "Source of Truth" zu behalten und den Change Feed als Synchronisationsbus zu nutzen, ist **die richtige Entscheidung für 90% aller Geschäftsanwendungen**. Vorteile:
- Direktes Debugging über SQL-Queries auf den aktuellen Zustand
- Kein Replay nötig, um den aktuellen State zu lesen
- Bestehende Tools (pgAdmin, Metabase, etc.) funktionieren sofort
- Neue Entwickler verstehen das System in Minuten, nicht Tagen

### 1.2 Atomare Write-Semantik
CRUD-Mutation + Change Feed Append in **einer Transaktion** eliminiert das Dual-Write-Problem vollständig. Das ist der wichtigste architektonische Baustein und korrekt umgesetzt.

### 1.3 Trennung Change Events vs. Business Events
Architektonisch wertvoll und selten in vergleichbaren Systemen zu finden. Verhindert die häufige Falle, Login-Events oder Tracking-Events als Zustandsänderungen zu modellieren.

### 1.4 Projection-seitige Versionierung
Der Verzicht auf eine zentrale Upcasting-Pipeline zugunsten lokaler Version-Interpretation in Projections ist für dieses System die richtige Wahl. Die Begründung in `05-versionierung.md` ist überzeugend.

### 1.5 Phasenplan-Disziplin
*"Erst nach 3 echten Projections abstrahieren"* – diese Disziplin verhindert das häufigste Scheitern von Framework-Projekten: Abstraktion vor Erfahrung.

---

## 2. Kritische Findings (Sicherheit & Korrektheit)

### 🔴 S1: SQL-Injection-Risiko durch String-Konkatenation bei Event-Typen

**Betroffene Dokumente:** `03-change-feed.md`, `04-projections.md`

**Problem:** Die `EventTypes`-Collection in `IProjectionHandler` wird als `string[]` an die SQL-Query übergeben (`ANY(@eventTypes)`). Das ist korrekt parametrisiert. **Aber:** Die Event-Type-Strings selbst (`"UserEmailUpdated"`, etc.) werden nirgends validiert. Wenn ein Aufrufer des `ChangeWriter` einen manipulierten `eventType`-String übergibt, wird dieser zwar parametrisiert gespeichert (kein direktes SQL-Injection-Risiko), aber:

- Ein bösartiger Event-Type könnte Projections verwirren oder zum Absturz bringen
- Event-Types werden in Logs geschrieben – Log-Injection ist möglich
- Event-Types werden in `projection_failures` gespeichert – dort könnten sie in Admin-UIs angezeigt werden (XSS-Vektor)

**Empfehlung:** Event-Type-Validierung einführen (Regex: `^[A-Za-z][A-Za-z0-9_]{2,100}$`). Entweder im `ChangeWriter` oder als Konvention mit Compile-Time-Check (z.B. `static class EventTypes` mit Konstanten).

**Schweregrad:** Mittel – kein direktes SQL-Injection, aber Defense-in-Depth fehlt.

---

### 🔴 S2: Fehlende Autorisierungsprüfung im `GdprProcessor`

**Betroffenes Dokument:** `06-gdpr.md`

**Problem:** `RedactEntityAsync()` und `GetEntityHistoryAsync()` haben **keine Autorisierungsprüfung**. Jeder Code-Pfad, der Zugriff auf den `GdprProcessor` hat, kann beliebige Entities redacten oder deren komplette Historie abrufen.

**Risiken:**
- Ein kompromittierter Service könnte alle User-Daten redacten (Denial of Service durch Datenverlust)
- `GetEntityHistoryAsync` gibt **alle** Events zurück, inklusive potenziell sensibler Daten – ohne Prüfung, ob der Aufrufer berechtigt ist
- Kein Audit-Trail der Redaktion selbst (wer hat wann welche Daten redacted?)

**Empfehlung:**
1. Autorisierungs-Parameter (`actorId`, `reason`) als Pflichtparameter in `RedactEntityAsync()`
2. Jede Redaktion als eigenes Event im `business_event_log` protokollieren
3. Rate-Limiting für Redaktions-Operationen
4. `GetEntityHistoryAsync` nur über einen autorisierten Endpoint exponieren

**Schweregrad:** Hoch – DSGVO-Compliance erfordert Nachvollziehbarkeit der Löschung selbst.

---

### 🔴 S3: `actor_id` wird nicht validiert und ist optional

**Betroffene Dokumente:** `02-datenbank.md`, `03-change-feed.md`

**Problem:** `actor_id` ist `NULL`-fähig und wird nicht validiert. Im Beispiel-Code (`UpdateUserEmailHandler`) wird `actorId: null` übergeben. Das bedeutet:

- In Produktion könnten Änderungen ohne Akteur-Zuordnung entstehen
- Der Audit-Trail hat Lücken – "Wer hat diese Änderung gemacht?" ist nicht beantwortbar
- Bei einem Sicherheitsvorfall fehlt die Forensik-Grundlage

**Empfehlung:**
1. `actor_id` als **Pflichtfeld** im `ChangeWriter` (nicht nullable)
2. Fallback-Wert `"unknown"` ist **nicht** akzeptabel – lieber den Write ablehnen
3. In der Middleware/Pipeline den `actor_id` aus dem Authentication-Context extrahieren und automatisch setzen
4. Für System-Prozesse explizit `"system:scheduler"`, `"system:migration"` etc. verwenden

**Schweregrad:** Hoch – Audit-Trail-Lücken sind ein Compliance-Risiko.

---

### 🔴 S4: Keine Payload-Größenbeschränkung

**Betroffene Dokumente:** `03-change-feed.md`

**Problem:** Der `ChangeWriter` akzeptiert beliebig große `payloadJson`-Strings. Ein einzelnes Event mit einem 100 MB Payload würde:
- Die Datenbank belasten (JSONB-Parsing, TOAST-Kompression)
- Den Projection Worker blockieren (Deserialisierung)
- Potenziell den Connection-Pool erschöpfen

**Empfehlung:**
1. Payload-Größenlimit im `ChangeWriter` (z.B. 256 KB, konfigurierbar)
2. Für große Daten: Referenz-Pattern (Payload enthält nur eine URL/ID, Daten liegen in Blob-Storage)
3. Monitoring der durchschnittlichen Payload-Größe

**Schweregrad:** Mittel – DoS-Vektor bei unkontrolliertem Input.

---

## 3. Architektur-Findings (Korrektheit & Robustheit)

### 🟡 A1: `xmin`-Filter ist PostgreSQL-Versions-sensitiv

**Betroffenes Dokument:** `04-projections.md`

**Problem:** Der `xmin`-basierte Sichtbarkeitsfilter verwendet:
```sql
AND xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
```

- `pg_current_snapshot()` existiert erst ab **PostgreSQL 13** (vorher: `txid_current_snapshot()`)
- Die Doppel-Cast-Kette `xmin::text::bigint` ist fragil und kann bei 32-Bit-Wraparound von Transaction-IDs (nach ~4 Milliarden Transaktionen) zu Fehlern führen
- PostgreSQL's `xmin` ist ein 32-Bit-Wert, `pg_snapshot_xmin()` gibt `xid8` (64-Bit) zurück – der Vergleich über `::text::bigint` funktioniert, ist aber nicht offiziell dokumentiert

**Empfehlung:**
1. Minimale PostgreSQL-Version dokumentieren (≥ 13, besser ≥ 14)
2. Alternativ: `pg_visible_in_snapshot(xmin, pg_current_snapshot())` verwenden (sauberer, aber erst ab PG 14)
3. Einen Integrationstest schreiben, der parallele Writes + Polling testet

**Schweregrad:** Mittel – funktioniert in der Praxis, aber die Fragilität sollte dokumentiert sein.

---

### 🟡 A2: Checkpoint-Lücke bei gefilterten Event-Types

**Betroffenes Dokument:** `04-projections.md`

**Problem:** Die `LoadChangesAsync`-Query filtert nach `event_type = ANY(@eventTypes)`. Wenn eine Projection nur `UserEmailUpdated` abonniert, aber zwischen Checkpoint 100 und 200 nur `AssetCreated`-Events liegen, passiert Folgendes:

1. Worker pollt: `WHERE sequence_id > 100 AND event_type = ANY('UserEmailUpdated')` → 0 Ergebnisse
2. Worker wartet `PollInterval`, pollt erneut → wieder 0
3. Checkpoint bleibt bei 100, obwohl der Feed bei 200 ist

Das ist **korrekt** – aber es bedeutet, dass der Projection-Lag-Monitor (aus `07-projektstruktur.md`) für diese Projection einen hohen Lag anzeigt, obwohl sie eigentlich "up to date" ist. Das führt zu **False-Positive-Alerts**.

**Empfehlung:**
1. Lag-Berechnung berücksichtigt nur Events, die für die jeweilige Projection relevant sind
2. Oder: Checkpoint wird auch bei "keine relevanten Events" auf die höchste gesehene `sequence_id` gesetzt (erfordert eine zweite Query oder Anpassung der Batch-Logik)
3. Alternativ: Lag-Query filtert ebenfalls nach `event_type`

**Schweregrad:** Mittel – operatives Problem, kein Datenverlust.

---

### 🟡 A3: Dead-Letter-Events werden still übersprungen

**Betroffenes Dokument:** `04-projections.md`

**Problem:** Wenn ein Event `MaxAttemptsPerEvent` erreicht, wird der Checkpoint weitergesetzt und das Event nie wieder verarbeitet. Das ist das korrekte Verhalten für Poison Events, aber:

- Es gibt keinen **Alerting-Mechanismus** – das Team erfährt möglicherweise nie, dass Events übersprungen wurden
- Es gibt keinen **manuellen Retry-Pfad** – wie wird ein Dead-Letter-Event nach einem Bugfix erneut verarbeitet?
- Die `projection_failures`-Tabelle wächst unbegrenzt

**Empfehlung:**
1. Dead-Letter-Events lösen einen Alert aus (Log-Level `Critical`, Metrik, Webhook)
2. Ein Admin-Endpoint oder CLI-Tool zum manuellen Retry einzelner Dead-Letter-Events
3. Retention-Policy für `projection_failures` (z.B. gelöste Failures nach 30 Tagen löschen)
4. Dashboard-Query für "aktive Dead Letters" in der Monitoring-Sektion dokumentieren

**Schweregrad:** Mittel – operatives Risiko bei stillem Datenverlust.

---

### 🟡 A4: `NpgsqlUnitOfWork` fehlt Retry-Logik für transiente Fehler

**Betroffenes Dokument:** `03-change-feed.md`

**Problem:** Die `NpgsqlUnitOfWork`-Implementierung hat kein Retry bei transienten Datenbankfehlern (Connection-Timeout, Deadlock, Serialization Failure). In Produktion unter Last sind diese Fehler normal.

**Empfehlung:**
1. Retry mit Polly oder manueller Schleife für transiente `NpgsqlException`-Codes (z.B. `40001` Serialization Failure, `40P01` Deadlock)
2. Maximale Retry-Anzahl konfigurierbar (z.B. 3)
3. Jitter beim Retry-Delay, um Thundering-Herd zu vermeiden

**Schweregrad:** Mittel – wird erst unter Last relevant, aber dann kritisch.

---

### 🟡 A5: Keine Idempotenz-Garantie im `ChangeWriter`

**Betroffenes Dokument:** `03-change-feed.md`

**Problem:** Wenn ein HTTP-Request durch einen Retry (z.B. Load-Balancer-Timeout) doppelt ankommt, wird das CRUD-Update + Change-Feed-Append **doppelt** ausgeführt. Der Change Feed enthält dann zwei identische Events.

**Empfehlung:**
1. `correlation_id` als Idempotenz-Key nutzen: `INSERT INTO change_feed ... ON CONFLICT (correlation_id, entity, entity_id, event_type) DO NOTHING`
2. Oder: Unique-Constraint auf `(correlation_id, entity, entity_id, event_type)` mit partiellem Index `WHERE correlation_id IS NOT NULL`
3. Alternativ: Idempotenz auf HTTP-Ebene (Idempotency-Key-Header)

**Schweregrad:** Mittel – relevant bei Retry-Szenarien in Produktion.

---

## 4. DSGVO-spezifische Findings

### 🟡 D1: `business_event_log` wird bei Redaktion nicht berücksichtigt

**Betroffene Dokumente:** `06-gdpr.md`, `02-datenbank.md`

**Problem:** Die DSGVO-Checkliste in `06-gdpr.md` erwähnt "Business Event Log für die Entity bereinigt (falls personenbezogene Daten enthalten)", aber:
- Der `GdprProcessor` hat keine Methode dafür
- `business_event_log` hat kein `redacted`-Flag
- `business_event_log` verwendet `aggregate_id` statt `entity`/`entity_id` – die Zuordnung ist nicht trivial

**Empfehlung:**
1. `RedactEntityAsync()` erweitern um Business-Event-Log-Bereinigung
2. Oder: Separaten `BusinessEventRedactor` implementieren
3. `business_event_log` um `redacted BOOLEAN DEFAULT FALSE` erweitern
4. Konsistente Namensgebung: `aggregate_id` vs. `entity_id` vereinheitlichen

**Schweregrad:** Hoch – DSGVO-Lücke bei personenbezogenen Business Events.

---

### 🟡 D2: Redaktion ist nicht atomar mit Domain-Löschung

**Betroffenes Dokument:** `06-gdpr.md`

**Problem:** In `UserDeletionService.DeleteUserAsync()` sind die drei Schritte (Domain löschen, Feed redacten, Read Models bereinigen) **nicht in einer Transaktion**. Wenn der Prozess nach Schritt 1 crasht:
- User ist aus der Domain-Tabelle gelöscht
- Change Feed enthält noch die originalen Payloads
- Read Models enthalten noch die Daten

**Empfehlung:**
1. Mindestens Schritt 1 + 2 in einer Transaktion (Domain-Löschung + Feed-Redaktion)
2. Schritt 3 (Read-Model-Bereinigung) kann asynchron sein, muss aber **garantiert** nachgeholt werden (z.B. über einen "Pending Deletion"-Status)
3. Alternativ: Outbox-Pattern für die Löschung – Löschauftrag in eine Tabelle schreiben, Worker führt alle Schritte aus

**Schweregrad:** Mittel-Hoch – Teilweise Löschung ist ein DSGVO-Risiko.

---

### 🟡 D3: Keine Retention-Policy für den Change Feed

**Betroffene Dokumente:** `02-datenbank.md`, `06-gdpr.md`

**Problem:** Der Change Feed wächst unbegrenzt. Art. 5 Abs. 1 lit. e DSGVO fordert Speicherbegrenzung. Das SQL-Beispiel in `06-gdpr.md` zeigt eine zeitbasierte Redaktion, aber:
- Es gibt keinen automatisierten Prozess dafür
- Nicht-personenbezogene Events (z.B. `AssetCreated`) wachsen ebenfalls unbegrenzt
- Partitionierung wird nicht erwähnt

**Empfehlung:**
1. Retention-Policy als Betriebsinvariante definieren (z.B. "Events älter als X Jahre werden archiviert/gelöscht")
2. PostgreSQL Table Partitioning nach `timestamp` (monatlich/jährlich) für effizientes Löschen alter Partitionen
3. Archivierungs-Strategie: Alte Events in Cold Storage (S3, Azure Blob) vor dem Löschen

**Schweregrad:** Mittel – wird erst bei Wachstum relevant, sollte aber von Anfang an geplant sein.

---

## 5. Betriebsreife-Findings

### 🟡 O1: Kein Graceful Shutdown für Projection Workers

**Betroffenes Dokument:** `04-projections.md`

**Problem:** Der `ProjectionWorker` fängt `OperationCanceledException` ab und bricht die Schleife ab. Aber:
- Wenn der Worker gerade mitten in einer Batch-Verarbeitung ist, wird die aktuelle Transaktion durch den `CancellationToken` abgebrochen
- Das ist korrekt (Rollback), aber es bedeutet, dass der letzte Batch **komplett** wiederholt wird
- Bei großen Batches (100 Events) kann das zu unnötiger Doppelverarbeitung führen

**Empfehlung:** Dokumentieren, dass Graceful Shutdown den aktuellen Batch abschließt, bevor der Worker stoppt. Alternativ: `stoppingToken` nur zwischen Events prüfen, nicht innerhalb der Handler-Ausführung.

---

### 🟡 O2: Keine Metrik-Exposition

**Betroffenes Dokument:** `07-projektstruktur.md`

**Problem:** Der Health-Check-Endpoint ist ein guter Anfang, aber für Produktion fehlen:
- Prometheus/OpenTelemetry-Metriken (Events/Sekunde, Projection-Lag, Fehlerrate)
- Distributed Tracing (Correlation-ID durch den gesamten Pfad)
- Structured Logging mit Event-Kontext

**Empfehlung:** Als Phase-2-Ergänzung OpenTelemetry-Integration planen. Die `correlation_id` ist bereits vorhanden – sie muss nur in den Tracing-Context propagiert werden.

---

### 🟡 O3: Kein Konzept für horizontale Skalierung

**Betroffenes Dokument:** `07-projektstruktur.md`

**Problem:** "Projection Leasing" wird als optionale Erweiterung erwähnt, aber nicht weiter beschrieben. Bei mehreren App-Instanzen (Kubernetes, Load-Balanced) laufen **mehrere Worker für dieselbe Projection** – das führt zu:
- Doppelter Verarbeitung
- Checkpoint-Konflikten
- Inkonsistenten Read Models

**Empfehlung:**
1. Explizit dokumentieren: "In Phase 1–2 läuft genau **eine** App-Instanz"
2. Für Phase 3+: Advisory Locks (`pg_advisory_lock`) oder Leader Election beschreiben
3. Alternativ: Projection-Worker als separaten Prozess deployen (nicht im Web-Host)

**Schweregrad:** Mittel – muss vor dem ersten Multi-Instance-Deployment gelöst sein.

---

## 6. Code-Qualität und Konsistenz

### 🟢 C1: Inkonsistente Namensgebung `aggregate_id` vs. `entity`/`entity_id`

`change_feed` verwendet `entity` + `entity_id`, `business_event_log` verwendet `aggregate_id`. Das ist verwirrend und erschwert übergreifende Queries.

**Empfehlung:** Einheitlich `entity` + `entity_id` verwenden oder die Abweichung explizit begründen.

---

### 🟢 C2: `ChangeRecord` in `06-gdpr.md` hat weniger Felder als in `03-change-feed.md`

In `GetEntityHistoryAsync()` wird ein `ChangeRecord` mit nur 7 Feldern konstruiert (ohne `CorrelationId`, `CausationId`, `ActorId`). Das ist inkonsistent mit der Definition in `03-change-feed.md` (10 Felder).

**Empfehlung:** Alle Felder lesen oder einen separaten `RedactedChangeRecord`-Typ verwenden.

---

### 🟢 C3: `ProjectionRegistry` verliert die Transaktion

In `05-versionierung.md` verwendet die `UserProjection` die `ProjectionRegistry`, aber `DispatchAsync()` übergibt **nicht** die `NpgsqlConnection` und `NpgsqlTransaction` an die Handler. Die `IVersionedHandler<T>`-Implementierungen haben keinen Zugriff auf die Transaktion.

**Empfehlung:** `IVersionedHandler<T>.HandleAsync()` um `NpgsqlConnection` + `NpgsqlTransaction` erweitern, oder die Registry so anpassen, dass sie die Transaktion durchreicht.

---

### 🟢 C4: `event_outbox`-Verarbeitung

Die in dieser Analyse markierte Luecke wurde inzwischen geschlossen: Neben `OutboxWriter` existiert jetzt ein **OutboxWorker**, der `event_outbox` pollt und ueber `IOutboxPublisher` zustellt.

---

## 7. Zukunftsfähigkeit

### Was gut vorbereitet ist:
- **Multi-Projection**: Jede Projection ist unabhängig – neue Projections können jederzeit hinzugefügt werden
- **Schema-Evolution**: Versionierung ist von Anfang an eingebaut
- **Replay**: Sauber implementiert, operativ nutzbar
- **Monitoring**: Grundlagen sind vorhanden (Lag-Query, Health-Check)

### Was fehlt für Enterprise-Readiness:
- **Multi-Tenancy**: Kein Konzept für mandantenfähige Systeme (Tenant-ID im Change Feed?)
- **Event-Archivierung**: Kein Konzept für langfristige Aufbewahrung vs. aktive Daten
- **Schema-Registry**: Bei >10 Event-Typen wird eine zentrale Dokumentation der Payload-Schemas nötig
- **Cross-Service-Events**: Wenn das System in Microservices aufgeteilt wird, fehlt ein Konzept für Service-übergreifende Events

Diese Punkte sind für Phase 1–3 nicht relevant, sollten aber als "Known Unknowns" dokumentiert werden.

---

## 8. Zusammenfassung der Empfehlungen nach Priorität

### Sofort (vor Phase 1 Produktion):
| # | Finding | Schweregrad |
|---|---------|-------------|
| S2 | Autorisierung + Audit für `GdprProcessor` | 🔴 Hoch |
| S3 | `actor_id` als Pflichtfeld | 🔴 Hoch |
| D1 | `business_event_log` in DSGVO-Redaktion einbeziehen | 🔴 Hoch |

### Vor Phase 2:
| # | Finding | Schweregrad |
|---|---------|-------------|
| S1 | Event-Type-Validierung | 🟡 Mittel |
| S4 | Payload-Größenlimit | 🟡 Mittel |
| A3 | Dead-Letter-Alerting | 🟡 Mittel |
| A4 | Retry-Logik für transiente DB-Fehler | 🟡 Mittel |
| D2 | Atomare DSGVO-Löschung | 🟡 Mittel-Hoch |
| O3 | Multi-Instance-Verhalten dokumentieren | 🟡 Mittel |

### Vor Phase 3:
| # | Finding | Schweregrad |
|---|---------|-------------|
| A1 | PostgreSQL-Mindestversion dokumentieren | 🟡 Mittel |
| A2 | Lag-Berechnung für gefilterte Projections | 🟡 Mittel |
| A5 | Idempotenz im ChangeWriter | 🟡 Mittel |
| D3 | Retention-Policy + Partitionierung | 🟡 Mittel |
| O2 | OpenTelemetry-Integration | 🟡 Mittel |

### Nice-to-have:
| # | Finding | Schweregrad |
|---|---------|-------------|
| C1 | Namensgebung vereinheitlichen | 🟢 Niedrig |
| C2 | `ChangeRecord`-Konsistenz in GDPR | 🟢 Niedrig |
| C3 | Transaktion in `ProjectionRegistry` durchreichen | 🟢 Niedrig |
| C4 | Outbox-Publisher als offenen Punkt markieren | ✅ Erledigt |
| O1 | Graceful Shutdown dokumentieren | 🟢 Niedrig |

---

## 9. Fazit

Die Architektur ist **solide, pragmatisch und gut begründet**. Die wichtigsten Entscheidungen (CRUD Truth, atomare Writes, asynchrone Projections, Redaktion statt Crypto-Deletion) sind korrekt und zukunftsfähig.

~~Die kritischsten offenen Punkte betreffen **Sicherheit und DSGVO-Vollständigkeit**:~~
~~1. Der `GdprProcessor` braucht Autorisierung und Audit~~
~~2. `actor_id` muss ein Pflichtfeld werden~~
~~3. `business_event_log` muss in die DSGVO-Redaktion einbezogen werden~~

**Alle kritischen Punkte wurden adressiert.** Das System ist **produktionsreif für Phase 1–2**.

> **Kernaussage:** Die Architektur ist besser als 80% der Event-Sourcing-Implementierungen, die ich in der Praxis gesehen habe – gerade weil sie kein pures Event Sourcing ist.

---

## 10. Umsetzungsstatus

Alle Findings wurden in die Implementierungsdokumentation eingearbeitet:

| Finding | Status | Eingearbeitet in |
|---|---|---|
| **S1:** Event-Type-Validierung | ✅ Umgesetzt | `03-change-feed.md` (ChangeWriter mit Regex-Validierung) |
| **S2:** Autorisierung im GdprProcessor | ✅ Umgesetzt | `06-gdpr.md` (actorId + reason als Pflichtparameter, Audit-Event) |
| **S3:** actor_id als Pflichtfeld | ✅ Umgesetzt | `02-datenbank.md` (NOT NULL), `03-change-feed.md` (Pflichtparameter) |
| **S4:** Payload-Größenlimit | ✅ Umgesetzt | `03-change-feed.md` (ChangeWriterOptions, 256 KB Default) |
| **D1:** business_event_log in DSGVO | ✅ Umgesetzt | `02-datenbank.md` (redacted-Flag, entity/entity_id), `06-gdpr.md` (GdprProcessor redacted beide Tabellen) |
| **D2:** Atomare DSGVO-Löschung | ✅ Umgesetzt | `06-gdpr.md` (Transaktion für Feed + Business Event Log) |
| **D3:** Retention-Policy | ✅ Dokumentiert | `06-gdpr.md` (SQL-Beispiele für beide Tabellen, Partitionierungs-Empfehlung) |
| **A1:** PostgreSQL-Mindestversion | ✅ Dokumentiert | `02-datenbank.md` (PG ≥ 14) |
| **A2:** Lag-Berechnung | 📋 Offen | Für Phase 2 geplant |
| **A3:** Dead-Letter-Alerting | 📋 Offen | Für Phase 2 geplant |
| **A4:** Retry-Logik UnitOfWork | ✅ Umgesetzt | `03-change-feed.md` (NpgsqlUnitOfWork mit transientem Retry + Jitter) |
| **A5:** Idempotenz ChangeWriter | 📋 Offen | Für Phase 3 geplant |
| **C1:** Namensgebung vereinheitlichen | ✅ Umgesetzt | `02-datenbank.md` (aggregate_id → entity + entity_id) |
| **C2:** ChangeRecord-Konsistenz GDPR | ✅ Umgesetzt | `06-gdpr.md` (alle 10 Felder werden gelesen) |
| **C3:** Transaktion in ProjectionRegistry | 📋 Offen | Für Phase 3 geplant |
| **C4:** Outbox-Publisher | ✅ Umgesetzt | `OutboxWorker` + `AddOutboxWorker<TPublisher>()` vorhanden |
| **O1:** Graceful Shutdown | 📋 Offen | Für Phase 2 geplant |
| **O2:** OpenTelemetry | 📋 Offen | Für Phase 2 geplant |
| **O3:** Multi-Instance-Verhalten | ✅ Dokumentiert | `07-projektstruktur.md` (Betriebsinvariante: eine Instanz in Phase 1–2) |
| **Multi-Tenancy** | ✅ Neu | `08-multi-tenancy.md` (Shared DB + RLS, Database-per-Tenant) |
