# Framework-Bewertung und Erweiterungsvorschläge

## Gesamteinschätzung

Papuma.Kernel ist ein **überraschend vollständiges und durchdachtes Framework** für seine Größe. Die Architekturentscheidungen sind konsistent, die Abstraktionen sind schlank, und die Implementierung folgt einem klaren Prinzip: *Explizitheit über Magie*.

### Was besonders gut gelungen ist

1. **Scope-Modell als Sicherheitsinvariante** — Die Kombination aus C#-Validierung (`ScopeContext`), SQL-CHECK-Constraints und RLS-Policies bildet eine dreifache Absicherung. Das ist selten so konsequent durchgezogen.

2. **GDPR als First-Class-Concern** — Die meisten Frameworks behandeln DSGVO als Afterthought. Hier ist Redaktion atomar, auditiert, und deckt beide Event-Tabellen ab. `actorId` + `reason` als Pflichtparameter ist eine kluge Designentscheidung.

3. **ProjectionWorker-Qualität** — Visibility Guard (`xmin < pg_snapshot_xmin`), Dead-Letter mit Exponential Backoff, Replay via Channel — das sind produktionsreife Details, die man in vielen größeren Frameworks nicht findet.

4. **Keine unnötigen Abstraktionen** — Kein Repository-Pattern, kein Generic-Host-Magic, kein Auto-Discovery. Der Entwickler sieht genau, was passiert.

---

## Wo das Framework den Entwickler unterstützt — und wo nicht

### Change Feed

| Aspekt | Unterstützung | Bewertung |
|---|---|---|
| Append in Transaktion | `ChangeWriter.AppendAsync()` | ✅ Vollständig |
| Input-Validierung | `InputValidator` mit Regex + Size-Check | ✅ Vollständig |
| Scope + RLS | Automatisches `SET LOCAL` vor jedem Insert | ✅ Vollständig |
| Lesen des Feeds | Nur im `ProjectionWorker` | ⚠️ Kein allgemeiner Reader |
| Schema-Migration | `schema.sql` als Datei | ⚠️ Kein Migrations-Runner |

### Projections

| Aspekt | Unterstützung | Bewertung |
|---|---|---|
| Checkpoint-Management | Automatisch im Worker | ✅ Vollständig |
| Fehlerbehandlung | Dead-Letter + Retry + Backoff | ✅ Vollständig |
| Replay | `ReplayService` + `IReplayableProjection` | ✅ Vollständig |
| Versioned Dispatch | `ProjectionRegistry` | ✅ Vollständig |
| Externe Projektionen | `IExternalProjectionHandler` Interface | ⚠️ Nur Interface, kein Worker |
| Projection-Monitoring | Kein Health-Check / Lag-Metrik | ❌ Fehlt |

### GDPR

| Aspekt | Unterstützung | Bewertung |
|---|---|---|
| Art. 17 Redaktion | Atomar, beide Tabellen, auditiert | ✅ Vollständig |
| Art. 15 Auskunft | Beide Tabellen, inkl. redacted | ✅ Vollständig |
| Read-Model-Bereinigung | Nicht im Kernel | ⚠️ Bewusst, aber dokumentiert |
| Retention Policy | Nur als SQL-Beispiel in der Doku | ⚠️ Kein Automatismus |

### Outbox

| Aspekt | Unterstützung | Bewertung |
|---|---|---|
| Enqueue in Transaktion | `OutboxWriter.EnqueueAsync()` | ✅ Vollständig |
| Publisher-Interface | `IOutboxPublisher` | ✅ Definiert |
| Dispatch-Worker | — | ❌ Fehlt |

### Multi-Tenancy

| Aspekt | Unterstützung | Bewertung |
|---|---|---|
| Shared DB + RLS | Schema + Writer-Integration | ✅ Vollständig |
| Database-per-Tenant | `ScopeDataSourceFactory` | ✅ Vollständig |
| Scope-Resolution (HTTP) | `ScopeMiddleware` + `IScopeResolver` | ✅ Vollständig |

---

## Erweiterungsvorschläge — priorisiert

### Priorität 1: Hoher Nutzen, geringer Aufwand

#### 1a. OutboxWorker — Polling-Dispatcher für die Outbox

**Problem:** Die Outbox hat Write-Seite (`OutboxWriter`) und Interface (`IOutboxPublisher`), aber keinen Worker, der `Pending`-Einträge pollt und an den Publisher übergibt. Jeder Nutzer muss diesen Worker selbst bauen.

**Vorschlag:** Ein `OutboxWorker : BackgroundService` analog zum `ProjectionWorker`:
- Pollt `event_outbox WHERE status = 'Pending' AND next_retry_at <= NOW()`
- Ruft `IOutboxPublisher.PublishAsync()` auf
- Setzt `status = 'Sent'` bei Erfolg, `status = 'Failed'` + `attempts++` bei Fehler
- Exponential Backoff wie beim ProjectionWorker
- Dead-Letter nach N Versuchen

**Aufwand:** Gering — das Pattern existiert bereits im `ProjectionWorker`.

```
┌─────────────────────────────────────────────┐
│              OutboxWorker                   │
│                                             │
│  Poll: event_outbox WHERE Pending           │
│       ↓                                     │
│  IOutboxPublisher.PublishAsync()            │
│       ↓                                     │
│  status = Sent / Failed + retry             │
└─────────────────────────────────────────────┘
```

#### 1b. ExternalProjectionWorker

**Problem:** `IExternalProjectionHandler` ist definiert (für Projektionen in externe Systeme wie Elasticsearch), aber es gibt keinen Worker dafür. Der bestehende `ProjectionWorker` erwartet `IProjectionHandler` mit `NpgsqlConnection`/`NpgsqlTransaction` — das passt nicht für externe Systeme.

**Vorschlag:** Ein `ExternalProjectionWorker` der `IExternalProjectionHandler.HandleAsync(record, ct)` aufruft — ohne Transaktions-Kopplung, aber mit demselben Checkpoint/Retry-Mechanismus.

**Aufwand:** Gering — fast identisch zum bestehenden Worker, nur ohne Transaktions-Parameter.

---

### Priorität 2: Mittlerer Nutzen, mittlerer Aufwand

#### 2a. Projection Health / Lag-Metriken

**Problem:** Es gibt keine Möglichkeit, den Projection-Lag programmatisch abzufragen. In Produktion ist das kritisch: *Wie weit hinkt meine Projection hinter dem Feed her?*

**Vorschlag:**
- `ProjectionWorker.GetLagAsync()` — Differenz zwischen aktuellem Checkpoint und `MAX(sequence_id)` im Feed
- Optional: `IHealthCheck`-Integration für ASP.NET Core
- Optional: Metriken via `System.Diagnostics.Metrics` (Counter/Gauge für processed events, lag, failures)

#### 2b. Change Feed Reader (allgemein)

**Problem:** Der Change Feed kann aktuell nur über den `ProjectionWorker` gelesen werden. Für Ad-hoc-Abfragen (Debugging, Admin-UI, Export) gibt es keinen allgemeinen Reader.

**Vorschlag:** Ein `ChangeFeedReader` mit:
- `GetByEntityAsync(scope, entity, entityId)` — alle Events für eine Entity
- `GetBySequenceRangeAsync(scope, from, to)` — Bereich lesen
- `GetLatestSequenceIdAsync()` — aktuellste Sequence

Das würde auch den `GdprProcessor.GetEntityHistoryAsync()` vereinfachen, der aktuell die SQL-Queries inline hat.

---

### Priorität 3: Sinnvoll, aber nicht dringend

#### 3a. Schema-Migrations-Unterstützung

**Problem:** `schema.sql` ist eine einzelne Datei. Bei Schema-Änderungen (z.B. neue Spalten) gibt es keinen Migrationspfad.

**Vorschlag:** Kein eigenes Migrations-Framework, aber:
- Versionierte SQL-Dateien (`V001__initial.sql`, `V002__add_scope.sql`)
- Ein `SchemaVersionChecker` der prüft, ob die DB auf dem erwarteten Stand ist
- Dokumentation, wie man das mit FluentMigrator oder dbmate kombiniert

#### 3b. Retention-Policy-Automatisierung

**Problem:** Retention ist nur als SQL-Beispiel dokumentiert. Für regulierte Systeme wäre ein automatisierter Prozess sinnvoll.

**Vorschlag:** Ein `RetentionWorker : BackgroundService` der konfigurierbar alte, bereits redacted Events physisch löscht oder archiviert.

#### 3c. Idempotenz-Unterstützung für den Change Feed

**Problem:** Wenn ein Entwickler `ChangeWriter.AppendAsync()` versehentlich zweimal aufruft (z.B. bei Retry auf Application-Ebene), entsteht ein Duplikat im Feed.

**Vorschlag:** Optionaler Idempotency-Key (z.B. `correlation_id` + `event_type` als Unique Constraint). Nicht als Default, sondern als Opt-in.

---

### Was man bewusst NICHT ergänzen sollte

| Idee | Warum nicht |
|---|---|
| Auto-Discovery von Projections | Widerspricht dem Explizitheitsprinzip |
| Generic Repository über dem Change Feed | Unnötige Abstraktion |
| Eigenes Migrations-Framework | Besser auf bestehende Tools verweisen |
| Snapshot-Mechanismus | Nicht nötig bei CRUD-Truth-Modell |
| Event-Upcasting-Pipeline | Bewusst ausgeschlossen, `ProjectionRegistry` reicht |
| Message-Broker-Integration im Kernel | Gehört in die Anwendungsschicht |

---

## Architektur-Diagramm: Ist-Zustand vs. Erweiterungen

```mermaid
graph TD
    subgraph Vorhanden
        CW[ChangeWriter]
        BEW[BusinessEventWriter]
        OW[OutboxWriter]
        PW[ProjectionWorker]
        RS[ReplayService]
        GP[GdprProcessor]
        SM[ScopeMiddleware]
        UoW[NpgsqlUnitOfWork]
        PR[ProjectionRegistry]
    end

    subgraph Vorgeschlagen - Prio 1
        OWK[OutboxWorker]
        EPW[ExternalProjectionWorker]
    end

    subgraph Vorgeschlagen - Prio 2
        PLag[Projection Lag / Health]
        CFR[ChangeFeedReader]
    end

    subgraph Vorgeschlagen - Prio 3
        RW[RetentionWorker]
        SVC[SchemaVersionChecker]
    end

    CW -->|schreibt| CF[(change_feed)]
    BEW -->|schreibt| BEL[(business_event_log)]
    OW -->|schreibt| EO[(event_outbox)]

    CF -->|liest| PW
    CF -->|liest| EPW
    CF -->|liest| CFR
    EO -->|liest| OWK

    PW -->|nutzt| PR
    PW -->|Lag| PLag
    PW -->|Reset| RS

    GP -->|redacted| CF
    GP -->|redacted| BEL

    CF -->|alte Daten| RW
    BEL -->|alte Daten| RW
```

---

## Fazit

Das Framework ist in seinem Kern **produktionsreif und architektonisch sauber**. Die wichtigsten Lücken sind:

1. **OutboxWorker** — die offensichtlichste Lücke, weil die Write-Seite komplett ist aber die Delivery-Seite fehlt
2. **ExternalProjectionWorker** — das Interface existiert bereits, der Worker fehlt
3. **Projection-Monitoring** — in Produktion unverzichtbar

Alle drei Erweiterungen folgen dem bestehenden Pattern des `ProjectionWorker` und erfordern keine Architekturänderungen. Sie machen das Framework *vollständiger*, ohne es *komplexer* zu machen — und das ist genau die richtige Balance für Papuma.Kernel.
