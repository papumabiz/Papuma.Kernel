# Papuma vNEXT — Implementierungsplan

Status: Aktiv (2026-06-11) · Grundlage: [architecture.md](architecture.md) + [ADR 001–013](adr/)

Abarbeitung in Phasen-Reihenfolge; Checkboxen direkt hier pflegen. Jede Phase nennt
ihre ADRs, ihre v1-Erntequellen (Kopieren + Anpassen, nie Referenzieren) und eine
Definition of Done. **Eine Phase ist erst fertig, wenn auch die Querschnitts-Checkliste
(unten) für sie erfüllt ist.**

---

## Leitplanken

- Branch `vnext`; v1-Code bleibt unangetastet liegen, bis Phase 10 ihn entfernt.
- Kein Migrationscode von v1 — der Reboot ist vollständig.
- Tests gegen **echtes PostgreSQL 18** (Testcontainers); keine Mock-Datenbank für
  Storage-Verhalten.
- Reihenfolge innerhalb einer Phase frei, Phasen-Reihenfolge verbindlich (spätere
  Phasen bauen auf früheren auf).

---

## Phase 0 — Projektgerüst

- [ ] Neue Projektstruktur: `Papuma.Kernel` (Namespaces `Store` / `Changes` /
      `Processing` / `Model`) + `Papuma.Kernel.AspNetCore` + Testprojekt
      (Architektur §3); v1-Projekte parallel im Solution-File belassen
- [ ] `Directory.Build.props` prüfen (net10.0, Nullable, Analyzer)
- [ ] CI: Build + Tests mit PostgreSQL-18-Testcontainer
- [ ] Testcontainers-Setup einmalig als Fixture (Datenbank pro Testklasse oder
      Respawn-Strategie festlegen)

**DoD:** Leere Projekte bauen, ein Dummy-Integrationstest gegen PG-18-Container läuft in CI.

## Phase 1 — Fundament: Tenancy-Ernte + Schema

ADRs: [001](adr/adr-001-postgresql-18-only.md) · Ernte: `Tenancy/ScopeContext`,
`ScopeType`, `ScopeFilter`, `ScopeConnectionExtensions`, `IScopeDataSourceFactory`,
`ScopeDataSourceFactory`, `Validation/InputValidator`, AspNetCore `ScopeMiddleware` + Extensions

- [ ] Tenancy-Dateien kopieren und anpassen (Namespaces, ggf. Naming `tenant_id`)
- [ ] Zwei-Schichten-Sicherheitsmodell beibehalten: explizite WHERE-Prädikate +
      RLS via `SET LOCAL` (Doku-Invarianten aus `ScopeConnectionExtensions` übernehmen)
- [ ] `EnsureSchemaAsync`: idempotentes DDL für `papuma.document`, `papuma.change`,
      Checkpoint-/Failure-Tabellen (Definition aus Architektur §4)
- [ ] RLS-Policies für alle neuen Tabellen (inkl. `'All'`-Scope für Worker)
- [ ] PG-Versionscheck beim Start: `server_version_num >= 180000`, sonst typisierte
      Exception (ADR-001)

**DoD:** `EnsureSchemaAsync` zweimal hintereinander fehlerfrei; RLS-Test: Tenant A
sieht Tenant-B-Zeilen nicht, auch ohne WHERE-Prädikat; Versionscheck-Test.

## Phase 2 — Write-Pfad-Spike: Save / Load / Delete + Diff-Engine

ADRs: [002](adr/adr-002-document-as-truth.md) · [003](adr/adr-003-write-path-concurrency.md) ·
[004](adr/adr-004-changerecord-diff-only.md) · Ernte: — (alles neu)

- [ ] `DocumentSession` (minimal): `LoadAsync`, `SaveAsync(doc, expectedVersion)`,
      `DeleteAsync(id, expectedVersion)`
- [ ] Write als Einzelstatement mit `RETURNING old.data, new.data, new.version`
      (Insert/Update/Delete-Varianten); **Spike-Ziel: RETURNING OLD/NEW gegen echtes
      PG 18 verifizieren** — das ist die riskanteste Annahme des Designs
- [ ] `ConcurrencyException` (erwartete + aktuelle Version, ADR-003-Nachtrag) und
      `DocumentNotFoundException`
- [ ] Diff-Engine: reversibles Feld-Diff (`{path: {old, new}}`, Pfad-Syntax inkl.
      Arrays, ADR-004); Property-Tests: `apply(diff, old) == new` und
      `applyReverse(diff, new) == old`
- [ ] ChangeRecord-Insert in derselben Transaktion (operation, version,
      schema_version, diff, metadata, occurred_at, txid via `pg_current_xact_id()`)
- [ ] Unique-Index `(tenant, type, id, version)` auf `papuma.change` greift

**DoD:** Konflikttest (zwei Writer, einer verliert typisiert); Diff-Roundtrip-Tests;
Dokument + ChangeRecord nie inkonsistent (Rollback-Test mit künstlichem Fehler nach
UPDATE, vor Change-Insert).

## Phase 3 — Metamodell + Policies + Keys

ADRs: [006](adr/adr-006-keys-and-constraints.md) · [007](adr/adr-007-privacy-policies.md) ·
Ernte: konzeptionell `Gdpr/SensitiveRef`-Gedanke; Code neu

- [ ] Metamodell-Registry beim Start (Reflection): Typname, CLR-Typ, Properties,
      Policies, Keys, SchemaVersion (Architektur §6)
- [ ] Attribute: `[SensitiveData]`, `[TrackReference]`, `[TrackHash]`, `[DoNotTrack]`,
      `[UniqueKey]`
- [ ] Fluent-Builder mit Vorrang vor Attributen (ADR-007)
- [ ] Policy-Anwendung in der Diff-Engine: Track / Redact / Reference / Hash /
      DoNotTrack — inkl. Delete-Diffs (letzter Zustand sensibler Felder nie im Klartext)
- [ ] Unique-/Lookup-Keys als partielle Expression-Indizes in `EnsureSchemaAsync`;
      `UniqueKeyViolationException` mit Key-Name (ADR-006)
- [ ] `LoadByKeyAsync` entlang deklarierter Keys

**DoD:** Pro Policy ein Test, der den Diff-Eintrag prüft; Unique-Konflikt wirft
typisiert; nicht registrierter Dokumenttyp schlägt beim Save laut fehl.

## Phase 4 — Schema-Evolution

ADRs: [005](adr/adr-005-schema-evolution.md) · Ernte: —

- [ ] `schema_version` durch den gesamten Pfad (document, change, Save-Statement)
- [ ] Upcaster-Registrierung (Fluent), Kette n → n+1 → … beim Laden auf `JsonNode`
- [ ] Persistenz des neuen Stands erst beim nächsten Save
- [ ] Guard: Save mit niedrigerer `schema_version` als gespeichert → typisierter Fehler
- [ ] Additiv-Regel dokumentationsseitig in XML-Docs verankern (kein Code nötig)

**DoD:** Test: Dokument v1 laden → Upcaster läuft → Save schreibt v2; Guard-Test;
Upcaster-Ketten-Test über zwei Versionen.

## Phase 5 — Patch-Primitiv

ADRs: [012](adr/adr-012-partial-updates.md) · Ernte: —

- [ ] `PatchAsync` mit Katalog `Set` / `Remove` / `Increment` auf typisierten Pfaden
- [ ] SQL-Generierung via `jsonb_set` & Co., ein Statement, `RETURNING old/new`
- [ ] `expectedVersion` optional; ohne → Field-Level Last-Writer-Wins
- [ ] Optionaler Validator pro Typ: `new.data` deserialisieren + prüfen vor Commit
- [ ] Schema-Guard: Patch auf veraltete `schema_version` mit upcasting-betroffenem
      Pfad → typisierter Fehler
- [ ] Diff/Policies/ChangeRecord identisch zum Save-Pfad (gemeinsame Codebasis)

**DoD:** Paralleltest: zwei Patches auf verschiedene Felder konfligieren nicht,
Versionen bleiben linear; Validator-Rollback-Test; Policy-Test über Patch-Pfad
(`[TrackHash]`-Passwort).

## Phase 6 — Session als Unit of Work + Rollback

ADRs: [008](adr/adr-008-rollback-is-update.md) · Architektur §5 ("Session = Unit of Work") · Ernte: `Transactions/NpgsqlUnitOfWork` (Muster)

- [ ] Mehrere Saves/Patches/Appends pro Session in **einer** Transaktion;
      `CommitAsync` / implizites Rollback bei Dispose ohne Commit
- [ ] `correlationId` in `metadata` aller ChangeRecords einer Session (auto-generiert,
      überschreibbar); `causationId`/`actorId` als optionale Metadaten übernehmen
- [ ] `RollbackAsync(id, toVersion, expectedVersion)`: Diffs rückwärts anwenden,
      als Update mit `isRollback`/`restoredVersion` speichern
- [ ] Rollback über Schema-Versionen → durch Upcaster-Pipeline (ADR-008)
- [ ] Rollback policy-redacteter Felder: Wiederherstellung über Referenzquelle oder
      typisierter Fehler — nie stillschweigend falsche Werte

**DoD:** Registrierungs-Szenario (User + Address atomar, gemeinsame correlationId);
Rollback-Test inkl. append-only-Nachweis (Version 8 == Inhalt Version 3);
Redacted-Field-Rollback-Fehlertest.

## Phase 7 — Feed-Konsum + Processing-Engine

ADRs: [009](adr/adr-009-projections-as-dumb-handlers.md) ·
[010](adr/adr-010-feed-consumption.md) · Ernte: `Projections/ProjectionWorkerBase`
(Checkpoint-Upsert, Failure-Tabelle mit Backoff-SQL, Replay-Reset, Lag-Snapshot),
`ChangeFeed/ChangeFeedReader` (Keyset-Pagination-Muster), AspNetCore-Health-Checks

- [ ] `IChangeHandler` + Registrierung (`AddChangeHandler<T>()`)
- [ ] Worker-Skelett aus `ProjectionWorkerBase` ernten; Event-Typ-Filterung und
      `redacted`-Flag entfernen; Filterung optional nach `document_type`
- [ ] Snapshot-Lesen auf explizite `txid xid8`-Spalte umstellen
      (`txid < pg_snapshot_xmin(pg_current_snapshot())`, ADR-010 — ersetzt die
      `xmin::text::bigint`-Casts aus v1)
- [ ] `NOTIFY papuma_changes` am Ende der Save-Transaktion; LISTEN-Wakeup im Worker
      (Polling bleibt Wahrheit)
- [ ] Checkpoints, Retry/Backoff, Poison-Handling, Rebuild (Checkpoint-Reset) ernten
- [ ] Leader-Koordination konkurrierender Prozesse via `FOR UPDATE SKIP LOCKED`
      auf der Checkpoint-Tabelle
- [ ] Lag-Metrik + Health-Check (AspNetCore) portieren
- [ ] Komfort-Filter `WhenFieldChanged<T>(...)` als dünner Diff-Wrapper

**DoD:** Gap-Test (lang offene Transaktion mit kleinerer seq wird nicht übersprungen);
Wakeup-Latenz-Test (NOTIFY < Poll-Intervall); Rebuild-Test; Poison-Test (Handler wirft
n-mal → übersprungen + Failure-Eintrag); Health-Check liefert Lag.

## Phase 8 — Event-Log

ADRs: [013](adr/adr-013-business-event-log.md) · Ernte: Retention-Worker-Muster aus `Gdpr/RetentionWorker`

- [ ] Tabelle `papuma.event` (inkl. txid, RLS) in `EnsureSchemaAsync`
- [ ] `session.Append(...)` in der Session-Transaktion; Event-Typen im Metamodell
      registriert, Policies wirken auf Payload
- [ ] Konsum über dieselbe Engine: Handler abonnieren Change Feed, Event-Log oder
      beides (eigene Checkpoints pro Feed)
- [ ] Typ-spezifische Retention (Lösch-Worker, opt-in pro Event-Typ)
- [ ] Guard dokumentieren/testen: kein Upcasting für Events (unveränderliche Fakten)

**DoD:** Login-Szenario: `Append(UserLoggedIn)` + `Patch(lastLoginAt)` atomar mit
gemeinsamer correlationId; Retention-Test; Policy-Test auf Event-Payload
(`[SensitiveData]`-IP).

## Phase 9 — AspNetCore-Integration, DX und Doku

Ernte: `ScopeMiddleware`, `IScopeResolver`, Health-Check-Extensions

- [ ] `AddPapumaKernel(...)`-Bootstrap: DataSource, Metamodell-Build, EnsureSchema,
      Worker-Hosting
- [ ] Tenant-Resolution-Middleware portieren
- [ ] [Rezept Echtzeit-UI-Benachrichtigungen](recipes/realtime-ui-notifications.md)
      gegen die echte API verifizieren, Typnamen fixieren, Disclaimer entfernen
- [ ] README + Getting-Started für vNEXT; architecture.md gegen Implementierung
      abgleichen (Drift korrigieren)
- [ ] XML-Doku-Durchgang über die öffentliche API (AGENTS.md-Regeln)

**DoD:** Beispiel-App (Registrierungs-Szenario + Notifier) läuft end-to-end gegen
PG-18-Container.

## Phase 10 — v1-Rückbau

- [ ] v1-Quellverzeichnisse und -Tests entfernen (`Events/`, `Gdpr/` alt,
      `ChangeFeed/` alt, `Projections/` alt, `Schema/` alt — Tenancy-Originale erst
      jetzt, da vNEXT-Kopien etabliert)
- [ ] Solution/CI bereinigen; alte Tutorials unter `docs/tutorial/` als v1 archivieren
      oder entfernen
- [ ] Version bump, CHANGELOG/Release-Notes ("vNEXT-Reboot, keine Migration")

**DoD:** Repo enthält nur noch vNEXT-Code; Build, Tests, CI grün.

---

## Querschnitts-Checkliste (gilt für jede Phase)

- [ ] **Tenancy überall**: Jede neue Tabelle hat `tenant_id` + RLS-Policy; jede Query
      trägt explizite Scope-Prädikate (Schicht 1) **und** läuft nach `SetScopeAsync`
      (Schicht 2); Worker nutzen den `'All'`-Scope-Mechanismus
- [ ] **Policies können nicht umgangen werden**: Jeder Pfad, der einen ChangeRecord
      oder Event-Payload erzeugt, läuft durch die Policy-Anwendung (Save, Patch,
      Delete, Rollback, Append)
- [ ] Typisierte Exceptions mit Parametern, Validierung an der Boundary (AGENTS.md)
- [ ] License-Header + XML-Doku auf neuen öffentlichen Typen
- [ ] Integrationstests gegen PG 18, keine gemockten Storage-Pfade
- [ ] Keine neuen Paketabhängigkeiten ohne Not (BCL bevorzugen)

## Bekannte Risiken / offene Punkte

| # | Risiko | Behandlung |
|---|--------|-----------|
| 1 | `RETURNING OLD/NEW`-Verhalten (Syntax-Details, Interaktion mit `jsonb_set`) | Phase 2 ist bewusst der Spike dafür — frühestmöglich verifizieren |
| 2 | PG-18-Image-Verfügbarkeit in CI (Testcontainers) | In Phase 0 klären |
| 3 | Diff-Pfad-Syntax für Arrays (Index vs. Identität) | In Phase 2 entscheiden und in ADR-004 nachtragen |
| 4 | Performance der Diff-Engine bei großen Dokumenten | Benchmark in Phase 2; Limit-Empfehlung dokumentieren |
| 5 | `Reference`-Policy: eigener Sensitive Store nötig? | Start ohne (Referenz aufs Dokument); Bedarf nach Phase 8 neu bewerten (ADR-007) |
