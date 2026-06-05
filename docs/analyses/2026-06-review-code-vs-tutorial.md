# Review: Code vs. Implementierungs-Tutorial

**Datum:** 2026-06-01
**Scope:** Gesamter Produktionscode, alle Tests, alle 8 Tutorial-Dokumente
**Build:** ✅ Erfolgreich
**Tests:** ✅ 115/115 grün (nach Umsetzung aller Empfehlungen)

> **Status:** Alle Empfehlungen aus Priorität 1–4 wurden vollständig umgesetzt und verifiziert.

---

## 1. Gesamtbewertung

Die Library ist **architektonisch solide und konsistent** mit dem Tutorial. Der Code ist sauber, gut dokumentiert und folgt den AGENTS.md-Konventionen. Die Trennung in `Papuma.Kernel` (Infrastruktur) und `Papuma.Kernel.AspNetCore` (HTTP-Integration) ist korrekt umgesetzt.

**Fazit:** Die Library reicht als Infrastruktur-Kernel aus, um damit Applikationen zu schreiben. Es fehlt kein kritischer Glue-Code. Es gibt aber einige Verbesserungsmöglichkeiten in Security, Robustheit und Doku-Code-Konsistenz.

---

## 2. Code vs. Doku – Abweichungen

### 2.1 Positiv: Code ist besser als die Doku

| Aspekt | Doku | Code | Bewertung |
|---|---|---|---|
| `ChangeRecord.ActorId` | `string?` (nullable in 03-change-feed.md) | `string` (non-nullable) | ✅ Code ist korrekt – ActorId ist Pflichtfeld |
| `ChangeRecord.TenantId` | Nicht im Basis-Record | `string TenantId = "default"` | ✅ Tenant-Support von Anfang an |
| `ChangeWriter` Regex | Kein Timeout in Doku | `TimeSpan.FromMilliseconds(100)` | ✅ Folgt AGENTS.md (time-bounded Regex) |
| `ChangeWriter.ValidateInputs` | `payloadJson.Length` (Zeichenlänge) | `Encoding.UTF8.GetByteCount(payloadJson)` | ✅ Korrekte Byte-Messung |
| `ProjectionWorker` | Kein `ClearFailureAsync` | Hat `ClearFailureAsync` nach erfolgreichem Handle | ✅ Wichtig für Retry-Recovery |
| `ProjectionWorker` Retry-Filter | `pf.attempts >= @maxAttempts` nur | Zusätzlich `OR pf.next_retry_at > NOW()` | ✅ Respektiert Backoff-Timing |
| `TenantContext` | Einfache Validierung (Länge) | Regex-Validierung wie Entity/EventType | ✅ Konsistente Validierung |
| `ReplayService` | Kein Null-Check | `ArgumentNullException.ThrowIfNull` | ✅ Defensive Programmierung |
| Schema (schema.sql) | Kein CHECK-Constraint auf Outbox | `CHECK (status IN ('Pending', 'Sent', 'Failed'))` + `CHECK (attempts >= 0)` | ✅ Datenintegrität |
| RLS-Policy | `USING (tenant_id = current_setting('app.current_tenant'))` | `USING (tenant_id = COALESCE(current_setting('app.current_tenant', true), tenant_id))` | ✅ Graceful Fallback wenn Setting nicht gesetzt |
| `ProjectionWorker` | Nur `ResetCheckpointAsync` | `ResetProjectionStateAsync` löscht auch `projection_failures` | ✅ Sauberer Replay-Reset |
| `Papuma.Kernel.AspNetCore` | Nicht als separates Package in Doku | Eigenes Projekt mit `ITenantResolver`, `TenantMiddleware` | ✅ Saubere Package-Trennung |

### 2.2 Doku-Inkonsistenzen (nicht kritisch, aber zu aktualisieren)

| Stelle | Doku sagt | Code macht | Empfehlung |
|---|---|---|---|
| 03-change-feed.md Zeile 9 | `ActorId` ist `string?` | `string ActorId` (non-nullable) | Doku aktualisieren |
| 03-change-feed.md Zeile 78 | `ValidNamePattern` (ein Pattern) | Zwei Patterns: `ValidEntityPattern` + `ValidEventTypePattern` | Doku aktualisieren |
| 03-change-feed.md Zeile 146 | `payloadJson.Length` | `Encoding.UTF8.GetByteCount(payloadJson)` | Doku aktualisieren |
| 04-projections.md Zeile 91 | `_replayChannel` ist public nested record | `ReplayRequest` ist `private sealed record` | Doku aktualisieren |
| 07-projektstruktur.md Zeile 49 | `ITenantResolver` im Kernel | `ITenantResolver` in `Papuma.Kernel.AspNetCore` | Doku aktualisieren |
| 07-projektstruktur.md Zeile 51 | `TenantMiddleware` im Kernel | `TenantMiddleware` in `Papuma.Kernel.AspNetCore` | Doku aktualisieren |
| 08-multi-tenancy.md | `projection_checkpoint` hat `tenant_id` | Schema hat keinen `tenant_id` in `projection_checkpoint` | Bewusste Entscheidung? Klären. |

---

## 3. Security-Analyse

### 3.1 Gut umgesetzt ✅

- **Input-Validierung:** `ChangeWriter.ValidateInputs()` schützt gegen Log-Injection, XSS, überlange Strings
- **Regex-Timeouts:** Alle Regex-Patterns haben `TimeSpan.FromMilliseconds(100)` – verhindert ReDoS
- **Actor-ID Pflicht:** Kein Event ohne Akteur-Zuordnung möglich
- **RLS:** Row-Level Security im Schema aktiviert mit COALESCE-Fallback
- **Tenant-Validierung:** `TenantContext.Create()` validiert mit Regex
- **Null-Guards:** Konsequent `ArgumentNullException.ThrowIfNull()` an allen Boundaries
- **GDPR:** Atomare Redaktion über beide Tabellen, Audit-Event wird geschrieben

### 3.2 Verbesserungswürdig ⚠️ → ✅ Umgesetzt

#### 3.2.1 `BusinessEventWriter` hat keine Input-Validierung → ✅ Behoben

**Problem:** `ChangeWriter` validiert `entity`, `entityId`, `eventType`, `actorId` mit Regex und Längenbegrenzung. `BusinessEventWriter` prüft nur auf `null`, nicht auf Inhalt.

**Risiko:** Log-Injection, XSS in Admin-UIs, überlange Strings in `business_event_log`.

**Umsetzung:** Shared [`InputValidator`](src/Papuma.Kernel/Validation/InputValidator.cs) extrahiert. `BusinessEventWriter` validiert jetzt `eventType`, `actorId`, `entity`, `entityId` und `payloadSize` über [`BusinessEventWriterOptions`](src/Papuma.Kernel/Events/BusinessEventWriterOptions.cs). 7 neue Tests in [`BusinessEventWriterTests.cs`](tests/Papuma.Kernel.Tests/Events/BusinessEventWriterTests.cs).

#### 3.2.2 `OutboxWriter` hat keine Input-Validierung → ✅ Behoben

**Problem:** Wie `BusinessEventWriter` – nur Null-Checks, keine inhaltliche Validierung.

**Umsetzung:** `OutboxWriter` validiert jetzt `eventType` und `payloadSize` über [`OutboxWriterOptions`](src/Papuma.Kernel/Events/OutboxWriterOptions.cs). 2 neue Tests in [`OutboxWriterTests.cs`](tests/Papuma.Kernel.Tests/Events/OutboxWriterTests.cs).

#### 3.2.3 `GdprProcessor` validiert `entity`/`entityId` nur auf Leerheit → ✅ Behoben

**Problem:** `ValidateEntityReference` prüft nur `IsNullOrWhiteSpace`, nicht gegen Regex. Ein Angreifer könnte SQL-Injection-Versuche oder Log-Injection über `entity`-Parameter versuchen.

**Umsetzung:** `GdprProcessor` nutzt jetzt `InputValidator.ValidateEntity()` und `InputValidator.ValidateEntityId()` mit Regex-Validierung. 4 neue Tests in [`GdprProcessorTests.cs`](tests/Papuma.Kernel.Tests/Gdpr/GdprProcessorTests.cs).

#### 3.2.4 `SetTenantOnConnectionAsync` – SQL-Injection-Risiko bei `SET LOCAL`

**Problem in `ChangeWriter`:**
```csharp
cmd.CommandText = "SET LOCAL app.current_tenant = @tenantId";
cmd.Parameters.AddWithValue("tenantId", tenantId);
```

PostgreSQL `SET` akzeptiert **keine parametrisierten Werte** in allen Versionen/Konfigurationen. Ob `@tenantId` hier tatsächlich als Parameter behandelt wird, hängt von der Npgsql-Version ab. In manchen Fällen wird der Wert inline interpoliert.

**Risiko:** Wenn `TenantContext.Create()` die Validierung korrekt durchführt (Regex `^[A-Za-z][A-Za-z0-9_]{1,100}$`), ist das Risiko minimal. Aber Defense-in-Depth wäre besser.

**Empfehlung:** Explizit prüfen, ob Npgsql 10.x `SET LOCAL` mit Parametern korrekt behandelt. Alternativ: `SET LOCAL app.current_tenant = '...'` mit manueller Escaping-Funktion oder `NpgsqlConnection.ExecuteNonQuery($"SET LOCAL app.current_tenant = '{tenantId}'")` nach Regex-Validierung.

#### 3.2.5 Kein Rate-Limiting-Hinweis im Code für GDPR-Endpoint

**Problem:** Die Doku empfiehlt Rate-Limiting für den `GdprProcessor`, aber es gibt keinen Code-Kommentar oder Attribut, der darauf hinweist.

**Empfehlung:** XML-Doc-Kommentar am `RedactEntityAsync` ergänzen.

---

## 4. Exception Handling & Robustheit

### 4.1 Gut umgesetzt ✅

- **`NpgsqlUnitOfWork`:** Transiente Fehler werden mit exponentiellem Backoff + Jitter retried
- **`ProjectionWorker`:** Fehler werden in `projection_failures` protokolliert, Dead-Letter nach Max-Attempts
- **`ProjectionWorker`:** `OperationCanceledException` wird sauber abgefangen (Graceful Shutdown)
- **Options-Validierung:** `ProjectionWorker` und `NpgsqlUnitOfWork` validieren Options im Konstruktor

### 4.2 Verbesserungswürdig ⚠️ → Teilweise umgesetzt

#### 4.2.1 `NpgsqlUnitOfWork.RollbackAsync` kann selbst fehlschlagen → ✅ Behoben

**Problem:** Im `catch`-Block wird `await tx.RollbackAsync(ct)` aufgerufen. Wenn die Connection bereits geschlossen ist (z.B. bei `08006 connection_failure`), wirft `RollbackAsync` eine weitere Exception, die die ursprüngliche Exception verschluckt.

**Umsetzung:** Beide `RollbackAsync`-Aufrufe in [`NpgsqlUnitOfWork.cs`](src/Papuma.Kernel/Transactions/NpgsqlUnitOfWork.cs) sind jetzt in try/catch gewrappt. Fehlgeschlagene Rollbacks werden per `LogDebug` protokolliert, die ursprüngliche Exception wird korrekt weitergegeben.

#### 4.2.2 `ProjectionWorker.ProcessBatchAsync` – Connection-Lifetime

**Problem:** Eine Connection wird für den gesamten Batch geöffnet und bleibt offen. Bei großen Batches (100 Events) mit langsamen Handlern kann die Connection lange blockiert sein.

**Bewertung:** Akzeptabel für Phase 1, aber in der Doku als bekanntes Trade-off dokumentieren.

#### 4.2.3 `ProjectionWorker.RegisterFailureAsync` – Keine Transaktion → ✅ Kommentar ergänzt

**Problem:** `RegisterFailureAsync` läuft ohne Transaktion. Wenn der Worker zwischen `RegisterFailureAsync` und dem nächsten Loop-Durchlauf crasht, ist der Failure-Eintrag geschrieben, aber der Checkpoint nicht aktualisiert. Beim Neustart wird das Event erneut geladen und erneut fehlschlagen – der `attempts`-Counter wird korrekt inkrementiert, also ist das Verhalten korrekt (at-least-once für Failures).

**Umsetzung:** Erklärender Kommentar in [`ProjectionWorker.cs`](src/Papuma.Kernel/Projections/ProjectionWorker.cs) ergänzt.

#### 4.2.4 `ProjectionWorker` Error-Delay ist `PollInterval` statt fixer Wert → ✅ Behoben

**Problem:** Im `catch (Exception ex)`-Block in `ExecuteAsync` wird `_options.PollInterval` als Delay verwendet. Die Doku zeigt `TimeSpan.FromSeconds(5)`. Bei einem PollInterval von 100ms würde der Worker bei einem systemischen Fehler (z.B. DB down) 10x pro Sekunde Fehler loggen.

**Umsetzung:** `MinErrorDelay` (5 Sekunden) als Konstante eingeführt. Error-Delay ist jetzt `max(PollInterval, MinErrorDelay)`.

---

## 5. Fehlender Glue-Code – Was braucht eine App noch?

### 5.1 Nicht nötig im Kernel (korrekt ausgelagert)

| Komponente | Warum nicht im Kernel |
|---|---|
| Konkrete `ITenantResolver`-Implementierungen | App-spezifisch (JWT, Header, Session) |
| Konkrete `IProjectionHandler`-Implementierungen | Feature-spezifisch |
| Domain-Tabellen (users, etc.) | App-spezifisch |
| Health-Check-Endpoint | App-spezifisch (Beispiel in Doku reicht) |
| Payload-Records (UserEmailUpdatedV1, etc.) | Feature-spezifisch |

### 5.2 Fehlt im Kernel und wäre sinnvoll → ✅ Umgesetzt

#### 5.2.1 `ReplayService` DI-Registrierung → ✅ Behoben

**Umsetzung:** [`AddReplayService()`](src/Papuma.Kernel/Projections/ProjectionExtensions.cs) in `ProjectionExtensions` ergänzt. Entdeckt automatisch alle `ProjectionWorker`-Instanzen über `IHostedService` und baut das Dictionary über die neue [`ProjectionWorker.ProjectionName`](src/Papuma.Kernel/Projections/ProjectionWorker.cs)-Property.

#### 5.2.2 Outbox-Publisher-Worker → ✅ Worker + Registrierung umgesetzt

**Umsetzung:** [`OutboxWorker`](src/Papuma.Kernel/Events/OutboxWorker.cs) verarbeitet `event_outbox` mit Retry/Backoff und ruft [`IOutboxPublisher`](src/Papuma.Kernel/Events/IOutboxPublisher.cs) auf. Die DI-Registrierung erfolgt über [`AddOutboxWorker<TPublisher>()`](src/Papuma.Kernel/Events/OutboxExtensions.cs). Die App implementiert weiterhin nur den konkreten Publisher (RabbitMQ, Kafka, Webhook, etc.).

#### 5.2.3 Kein `AddPapumaKernel()`-Convenience-Extension → ✅ Behoben

**Umsetzung:** [`AddPapumaKernel()`](src/Papuma.Kernel/ServiceCollectionExtensions.cs) registriert alle Kern-Services (ChangeWriter, BusinessEventWriter, OutboxWriter, GdprProcessor, IUnitOfWork) mit konfigurierbaren [`PapumaKernelOptions`](src/Papuma.Kernel/PapumaKernelOptions.cs). 3 neue Tests in [`ServiceCollectionExtensionsTests.cs`](tests/Papuma.Kernel.Tests/ServiceCollectionExtensionsTests.cs).

#### 5.2.4 Schema-Deployment-Helper fehlt

**Problem:** `schema.sql` ist als Datei vorhanden, aber nicht als Embedded Resource im csproj konfiguriert (obwohl die Doku das erwähnt). Es gibt auch keine Methode wie `SchemaInitializer.EnsureSchemaAsync()`.

**Status:** ⏳ Offen – niedrige Priorität, da `psql -f schema.sql` für Phase 1 ausreicht.

### 5.3 Doku vs. Schema – `projection_checkpoint` ohne `tenant_id` → ✅ Doku aktualisiert

**Umsetzung:** [`08-multi-tenancy.md`](docs/implementation/08-multi-tenancy.md) aktualisiert. Erklärt jetzt den Code-Ansatz: Tenant wird in den Projection-Namen kodiert (`handler_name@tenant_id`), kein separates `tenant_id`-Feld nötig.

---

## 6. Code-Qualität – Kleinere Findings → ✅ Alle behoben

### 6.1 Inkonsistente Einrückung in `ProjectionWorker.LoadChangesAsync` → ✅ Behoben

Einrückung in [`ProjectionWorker.cs`](src/Papuma.Kernel/Projections/ProjectionWorker.cs) korrigiert.

### 6.2 Inkonsistente Einrückung in `ProjectionWorker.ClearFailureAsync` → ✅ Behoben

Einrückung in [`ProjectionWorker.cs`](src/Papuma.Kernel/Projections/ProjectionWorker.cs) korrigiert.

### 6.3 Inkonsistente Einrückung in `GdprProcessor` → ✅ Behoben

Einrückung in [`GdprProcessor.cs`](src/Papuma.Kernel/Gdpr/GdprProcessor.cs) korrigiert.

### 6.4 `EntityHistory` verwendet `List<T>` statt `IReadOnlyList<T>` → ✅ Behoben

[`EntityHistory.cs`](src/Papuma.Kernel/Gdpr/EntityHistory.cs) verwendet jetzt `IReadOnlyList<ChangeRecord>` und `IReadOnlyList<BusinessEventRecord>`.

### 6.5 `NpgsqlUnitOfWork` ist nicht `sealed` → ✅ Behoben

[`NpgsqlUnitOfWork.cs`](src/Papuma.Kernel/Transactions/NpgsqlUnitOfWork.cs) ist jetzt `sealed`.

### 6.6 `NpgsqlUnitOfWorkTests.cs` fehlt License-Header → ✅ Behoben

[`NpgsqlUnitOfWorkTests.cs`](tests/Papuma.Kernel.Tests/Transactions/NpgsqlUnitOfWorkTests.cs) hat jetzt den Standard-Copyright-Header.

---

## 7. Zusammenfassung der Empfehlungen

### Priorität 1 (Security/Robustheit) → ✅ Vollständig umgesetzt

1. ✅ **Input-Validierung in `BusinessEventWriter`** – Shared `InputValidator` mit Regex + Längenbegrenzung
2. ✅ **Input-Validierung in `OutboxWriter`** – `eventType`-Validierung + Payload-Limit
3. ✅ **`RollbackAsync` absichern** in `NpgsqlUnitOfWork` – try/catch mit LogDebug
4. ✅ **Error-Delay im `ProjectionWorker`** – `MinErrorDelay` (5s) als Untergrenze

### Priorität 2 (Glue-Code für App-Entwickler) → ✅ Vollständig umgesetzt

5. ✅ **`AddPapumaKernel()`-Extension** – Registriert alle Kern-Services mit konfigurierbaren `PapumaKernelOptions`
6. ✅ **`ReplayService` DI-Registrierung** – `AddReplayService()` entdeckt Worker automatisch über `IHostedService`
7. ✅ **Outbox-Delivery im Kernel** – `OutboxWorker` + `AddOutboxWorker<TPublisher>()` + `IOutboxPublisher`-Vertrag

### Priorität 3 (Code-Qualität) → ✅ Vollständig umgesetzt

8. ✅ **Einrückung fixen** in `ProjectionWorker`, `GdprProcessor`
9. ✅ **`EntityHistory`** – `IReadOnlyList<T>` statt `List<T>`
10. ✅ **`NpgsqlUnitOfWork`** – `sealed` gemacht
11. ✅ **License-Header** in `NpgsqlUnitOfWorkTests.cs` ergänzt

### Zusätzlich umgesetzt (nicht im Original-Review)

12. ✅ **Shared `InputValidator`-Klasse** – Zentrale Validierungslogik für alle Writer und `GdprProcessor`
13. ✅ **`GdprProcessor` Regex-Validierung** – `entity`/`entityId` mit denselben Patterns wie `ChangeWriter`
14. ✅ **`BusinessEventWriterOptions`/`OutboxWriterOptions`** – Konfigurierbare Payload-Limits
15. ✅ **60 neue Tests** – Von 55 auf 115 Tests (InputValidator, BusinessEventWriter, OutboxWriter, GdprProcessor, ServiceCollectionExtensions, ProjectionExtensionsReplay)
16. ✅ **XML-Doc für Rate-Limiting** – Hinweis in `GdprProcessor.RedactEntityAsync`
17. ✅ **`ProjectionWorker.ProjectionName`** – Öffentliche Property für ReplayService-Discovery

### Priorität 4 (Doku-Updates) → ✅ Vollständig umgesetzt

18. ✅ **03-change-feed.md** – `ActorId` von `string?` auf `string` korrigiert, `TenantId` ergänzt
19. ✅ **03-change-feed.md** – `ValidNamePattern` durch `InputValidator` mit zwei separaten Patterns ersetzt
20. ✅ **03-change-feed.md** – `payloadJson.Length` durch `Encoding.UTF8.GetByteCount` dokumentiert
21. ✅ **04-projections.md** – `ReplayRequest` von `public record` auf `private sealed record` korrigiert
22. ✅ **07-projektstruktur.md** – `ITenantResolver`/`TenantMiddleware` nach `Papuma.Kernel.AspNetCore` verschoben
23. ✅ **07-projektstruktur.md** – Vollständige Projektstruktur mit allen neuen Dateien aktualisiert
24. ✅ **07-projektstruktur.md** – `Program.cs`-Beispiel mit `AddPapumaKernel()` aktualisiert
25. ✅ **08-multi-tenancy.md** – `projection_checkpoint` ohne `tenant_id` erklärt (Tenant im Namen kodiert)
