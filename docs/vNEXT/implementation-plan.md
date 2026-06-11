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

## Phase 0 — Projektgerüst ✅ (2026-06-11)

- [x] Neue Projektstruktur: `Papuma.Kernel.Next` (+ `.AspNetCore`, + Tests) mit
      `RootNamespace Papuma.Kernel`; Umbenennung auf `Papuma.Kernel` erfolgt in
      Phase 10 beim v1-Rückbau. v1-Projekte unverändert in der Solution.
- [x] `Directory.Build.props` geprüft (net10.0, Nullable — unverändert tauglich)
- [x] CI: `vnext`-Branch in Trigger aufgenommen; Testcontainers nutzt das native
      Docker der ubuntu-Runner, keine Service-Container nötig
- [x] Testcontainers-Fixture: ein geteilter `postgres:18-alpine`-Container pro
      Test-Collection (`PostgresFixture`), inkl. Non-Superuser-Rolle für RLS-Tests.
      Lokal läuft Podman (`~/.testcontainers.properties`:
      `docker.host=npipe://./pipe/podman-machine-default`, `ryuk.disabled=true`)

**DoD erfüllt:** Build grün, Integrationstests laufen lokal gegen PG-18-Container (Podman).

## Phase 1 — Fundament: Tenancy-Ernte + Schema ✅ (2026-06-11)

ADRs: [001](adr/adr-001-postgresql-18-only.md) · Ernte: `Tenancy/ScopeContext`,
`ScopeType`, `ScopeFilter`, `ScopeConnectionExtensions`, `IScopeDataSourceFactory`,
`ScopeDataSourceFactory`, `Validation/InputValidator`, AspNetCore `ScopeMiddleware` + Extensions

- [x] Tenancy-Dateien kopiert (`ScopeContext`, `ScopeType`, `ScopeFilter`,
      `ScopeConnectionExtensions`, `IScopeDataSourceFactory`, `ScopeDataSourceFactory`,
      AspNetCore-Middleware; `InputValidator` dokumentenorientiert neu geschrieben).
      **Ernte-Fix:** v1s `SET LOCAL ... = @param` ist im Extended Protocol ungültig
      (konnte nie gegen echtes PG gelaufen sein) → ersetzt durch
      `set_config(..., is_local: true)`.
- [x] Zwei-Schichten-Sicherheitsmodell beibehalten (explizite Prädikate + RLS);
      zusätzlich `FORCE ROW LEVEL SECURITY`, damit auch der Table Owner RLS unterliegt
- [x] `EnsureSchemaAsync`: idempotentes DDL für `papuma.document`, `papuma.change`
      (inkl. `txid xid8`), `papuma.checkpoint`, `papuma.failure`
- [x] RLS-Policies für document/change inkl. `'All'`-Scope; checkpoint/failure bewusst
      ohne RLS (Handler-Infrastruktur ohne Tenant-Daten)
- [x] PG-Versionscheck in `EnsureSchemaAsync`: `server_version_num >= 180000`, sonst
      `PostgresVersionNotSupportedException` (ADR-001)

**DoD erfüllt:** Idempotenz-Test (2× EnsureSchema), 5 RLS-Isolationstests gegen
Non-Superuser-Rolle (Tenant/Platform/All/ohne Scope/WITH CHECK), Versionscheck-Tests.

## Phase 2 — Write-Pfad-Spike: Save / Load / Delete + Diff-Engine ✅ (2026-06-11)

ADRs: [002](adr/adr-002-document-as-truth.md) · [003](adr/adr-003-write-path-concurrency.md) ·
[004](adr/adr-004-changerecord-diff-only.md) · Ernte: — (alles neu)

- [x] **Spike bestanden**: `RETURNING old.data, new.data, new.version` gegen echtes
      PG 18 verifiziert (`SqlReturningSpikeTests`) — die riskanteste Design-Annahme trägt
- [x] `DocumentStore` / `DocumentSession` (minimal): `LoadAsync`, `SaveAsync(id, doc,
      expectedVersion)`, `DeleteAsync(id, expectedVersion)`; Dokumenttyp vorerst
      `typeof(T).Name` (Metamodell in Phase 3), `schema_version` fix 1 (Phase 4)
- [x] Write als Einzelstatement; Insert via `ON CONFLICT DO NOTHING` + Versions-Probe;
      **Neuanlage nach Delete setzt Versionszählung fort** (`max(change.version) + 1`,
      ADR-003-Nachtrag)
- [x] `ConcurrencyException` (Expected/Actual, Typ, Id) + `DocumentNotFoundException`;
      0-Treffer-Writes unterscheiden präzise zwischen beiden
- [x] `JsonDiffEngine`: reversibles Feld-Diff; **Arrays atomar**, Null ≠ Absent über
      Schlüssel-Anwesenheit kodiert (ADR-004-Nachtrag); Roundtrip-Tests
      `Apply`/`ApplyReverse` über Theorie-Fälle; Wire-Format `ToJson`/`FromJson`
- [x] ChangeRecord-Insert in derselben Transaktion; Diff gegen die von PG
      zurückgegebene (jsonb-normalisierte) Form gerechnet
- [x] Unique-Index greift — nachgewiesen durch den Atomizitätstest (geseedete
      Konflikt-Zeile → Save schlägt fehl → Dokument unverändert)

**DoD erfüllt:** Konflikttests (Update/Insert/Delete je typisiert, mit ActualVersion),
Diff-Roundtrips, Atomizitätstest, lückenlose Change-Historie inkl. Insert-nach-Delete,
Tenant-Isolation über Schicht-1-Prädikate. 48 Tests grün.

## Phase 3 — Metamodell + Policies + Keys ✅ (2026-06-11)

ADRs: [006](adr/adr-006-keys-and-constraints.md) · [007](adr/adr-007-privacy-policies.md) ·
Ernte: konzeptionell `Gdpr/SensitiveRef`-Gedanke; Code neu

- [x] Metamodell-Registry (`KernelModelBuilder` → `KernelModel`): Reflection-Scan beim
      Build, rekursiv in verschachtelte POCO-Typen (Zyklus-Guard; Collections bewusst
      nicht — Arrays sind atomare Diff-Werte), Pfade über die Serializer-Naming-Policy
- [x] Attribute: `[SensitiveData]`, `[TrackReference]`, `[TrackHash]`, `[DoNotTrack]`,
      `[UniqueKey]`, `[LookupKey]`
- [x] Fluent-Builder überschreibt Attribute; explizites `.Track()` setzt Defaults zurück;
      Policy-Auflösung über nächstgelegenen Ancestor-Pfad (Policy auf `address` deckt
      `address.city`)
- [x] `PolicyApplier` in beiden Write-Pfaden (Save + Delete) vor dem Change-Insert;
      Diff-Wire-Format erweitert: `{changed:true}` / `{ref}` / `{changed,hash}`;
      `Apply`/`ApplyReverse` werfen laut auf wertlosen Policy-Einträgen (ADR-008)
- [x] Keys als partielle Expression-Indizes in `EnsureSchemaAsync(ds, model)`
      (Identifier validiert, 63-Zeichen-Limit mit Hash-Disambiguierung);
      23505-Mapping über Constraint-Namen → `UniqueKeyViolationException` mit KeyPath
- [x] `LoadByKeyAsync` (deklarierte Keys only, LIMIT-2-Mehrfachtreffer-Guard);
      `SaveAsync(document, expectedVersion)` — Id kommt jetzt aus dem Metamodell
      (Konvention `Id`-Property oder `HasId(...)`)

**DoD erfüllt:** Pro Policy ein Diff-Eintrag-Test (inkl. Delete-Diff-Redaction und
Wire-Roundtrip durch die DB), Unique-Konflikt typisiert mit KeyPath, Unique-Key pro
Tenant gescoped, nicht registrierter Typ wirft `DocumentTypeNotRegisteredException`.
75 Tests grün.

## Phase 4 — Schema-Evolution ✅ (2026-06-11)

ADRs: [005](adr/adr-005-schema-evolution.md) · Ernte: —

- [x] `schema_version` semantisch korrekt durch alle Pfade: Load/LoadByKey lesen sie,
      Save schreibt die Modell-Version, **Delete-ChangeRecords tragen die gespeicherte
      (alte) Version** — der Diff-Inhalt hat altes Schema (ADR-005)
- [x] Upcaster-Registrierung (`.Upcast(fromVersion, json => ...)`); Kette wird beim
      Build auf Lückenlosigkeit ab Version 1 validiert; `SchemaVersion` = höchster
      fromVersion + 1; Duplikat-Guard
- [x] Lazy-Upcasting beim Laden auf dem rohen `JsonObject` vor der Deserialisierung;
      gespeicherte Zeile bleibt unangetastet (Test beweist `schema_version` bleibt 1)
- [x] Persistenz des gehobenen Stands beim nächsten Save (Test: v1 → Load → Save → v3)
- [x] Guard `SchemaVersionConflictException` auf Load, Save **und** Delete: gespeicherte
      Version > Modell-Version → typisierter Fehler; beim Save rollt das bereits
      angewendete UPDATE zurück (Test beweist unveränderten Zustand)
- [x] Additiv-Regel in den XML-Docs von `Upcast(...)` verankert (inkl. Verbot der
      additiven Simulation und Niemals-Löschen-Regel für Upcaster)

**DoD erfüllt:** Ketten-Test über zwei Versionen (v1: mail→email, v2: city→address.city),
Lazy-Test, Persist-Test, drei Guard-Tests (Load/Save/Delete) inkl. Rollback-Nachweis.
85 Tests grün.

## Phase 5 — Patch-Primitiv + Bulk-Operationen ✅ (2026-06-11)

ADRs: [012](adr/adr-012-partial-updates.md) · [014](adr/adr-014-bulk-operations.md) · Ernte: —

- [x] `PatchAsync(id, p => p.Set(...).Remove(...).Increment(...))` — typisierte Pfade,
      bewusst minimaler Katalog (`PatchBuilder<T>`)
- [x] SQL-Generierung: geschachtelte `jsonb_set`/`#-`-Ausdrücke, Pfade und Werte
      ausschließlich als Parameter gebunden; `Increment` liest atomar im Statement
      (`COALESCE((data #>> path)::numeric, 0) + n`); ein Statement, `RETURNING old/new`
- [x] `expectedVersion` optional (Field-Level LWW); mit Version → `ConcurrencyException`,
      0 Treffer ohne Version → `DocumentNotFoundException`
- [x] Validator pro Typ (`d.Validate(doc => ...)`): `new.data` wird vor dem Commit
      deserialisiert und geprüft — Wurf rollt das bereits angewendete UPDATE zurück
- [x] Schema-Guard: Patch auf veraltete `schema_version` → `SchemaUpcastRequiredException`
      (konservativ für alle Pfade — Upcaster sind opak; Load + Save hebt das Schema);
      gespeicherte Version > Modell weiterhin `SchemaVersionConflictException`
- [x] Diff/Policies/ChangeRecord teilen die Save-Codebasis (`PolicyApplier`,
      `InsertChangeRecordAsync`) — `[TrackHash]` wirkt über den Patch-Pfad identisch
- [x] Bulk: `PatchManyAsync(ids)` / `PatchWhereAsync(key, value)` (nur deklarierte
      Keys) / `DeleteManyAsync` / `DeleteWhereAsync`; set-basiertes `RETURNING` →
      ein ChangeRecord pro Dokument mit gemeinsamer `correlationId` in den Metadaten;
      Schema-Guard pro Zeile, ein Verstoß rollt alles zurück (atomar)

**DoD erfüllt:** Paralleltest (verschiedene Felder, lineare Versionen),
Validator-Rollback (kein Versions-Bump, kein ChangeRecord), `[TrackHash]` über Patch,
Upcast-Guard mit Load+Save-Heilung, Bulk: 3 Treffer → 3 ChangeRecords mit geteilter
correlationId, PatchWhere nur auf Treffer, Bulk-Atomizität (ein v1-Dokument rollt
alles zurück), Bulk-Konflikt gegen optimistischen Writer, DeleteWhere mit
Delete-Records, leere ID-Liste. 99 Tests grün.

## Phase 6 — Session als Unit of Work + Rollback ✅ (2026-06-11)

ADRs: [008](adr/adr-008-rollback-is-update.md) · Architektur §5 ("Session = Unit of Work") · Ernte: `Transactions/NpgsqlUnitOfWork` (Muster)

- [x] Session = eine Transaktion (lazy geöffnet, `SET LOCAL`-Scope pro Transaktion
      neu gesetzt); `CommitAsync` explizit, Dispose ohne Commit rollt zurück;
      Session ist `IAsyncDisposable`
- [x] **Savepoint pro Write**: Ein fehlgeschlagener Write (Konflikt, Unique-Verletzung,
      abgelehnter Validator) rollt nur sich selbst zurück — frühere Session-Writes
      bleiben intakt, die Session bleibt nutzbar (Test beweist es)
- [x] `correlationId` (auto/überschreibbar) + optional `actorId`/`causationId` via
      `SessionOptions` in den Metadaten **aller** ChangeRecords der Session;
      Bulk nutzt jetzt die Session-CorrelationId (ADR-014 vereinheitlicht)
- [x] `RollbackAsync(id, toVersion, expectedVersion)`: Diffs rückwärts anwenden
      (funktioniert auch über Delete/Recreate-Ketten), als Update mit
      `isRollback`/`restoredVersion`; Ziel-Version = Delete wird abgelehnt
- [x] Rollback über Schema-Versionen: rekonstruierter Zustand läuft durch die
      Upcaster-Pipeline, gespeichert wird im aktuellen Schema
- [x] Redacted/Reference/Hash-Einträge in der Historie →
      `RollbackNotPossibleException` (Pfad + Kind) — nie stillschweigend falsche Werte.
      Beachtenswert: Auch Insert-/Delete-Diffs sensibler Felder sind redacted und
      blockieren Rollback über solche Ketten — by design

**DoD erfüllt:** Registrierungs-Szenario (User + Address atomar, geteilte
correlationId), Dispose-Rollback-Test, Savepoint-Test, Metadata-Test
(actor/causation/correlation), Rollback append-only (v4 == Inhalt v1) mit Metadata,
Rollback über Delete-Kette, Delete-Ziel abgelehnt, Redacted-Fehlertest, stale
expectedVersion. 108 Tests grün.

## Phase 7 — Feed-Konsum + Processing-Engine ✅ (2026-06-11)

ADRs: [009](adr/adr-009-projections-as-dumb-handlers.md) ·
[010](adr/adr-010-feed-consumption.md) · Ernte: `Projections/ProjectionWorkerBase`
(Checkpoint-Upsert, Failure-Tabelle mit Backoff-SQL, Replay-Reset, Lag-Snapshot),
`ChangeFeed/ChangeFeedReader` (Keyset-Pagination-Muster), AspNetCore-Health-Checks

- [x] `IChangeHandler` + `ChangeRecord` (inkl. `FieldChanged`/`IsFieldTransition`-Sugar
      und typisiertem `FieldChanged<T>(x => x.Email)`-Extension); DI-Registrierung
      (`AddChangeHandler<T>()`) folgt mit dem Bootstrap in Phase 9
- [x] `ChangeFeedProcessor` neu (Ernte: Checkpoint-/Failure-/Backoff-SQL-Muster aus
      `ProjectionWorkerBase`, ohne Event-Typ-Filterung und `redacted`-Flag);
      **Stop-the-line-Semantik**: strikte seq-Ordnung pro Handler, Retry nach Backoff,
      Poison-Skip nach MaxAttempts mit bleibendem Failure-Eintrag
- [x] Snapshot-Lesen über explizite `txid xid8`-Spalte
      (`txid < pg_snapshot_xmin(pg_current_snapshot())`) statt v1s `xmin`-Casts
- [x] `NOTIFY papuma_changes` in `CommitAsync` (nur bei Writes, atomar mit dem Commit);
      LISTEN-Wakeup via `conn.WaitAsync(PollInterval)` — Polling bleibt Wahrheit
- [x] Checkpoints (`FOR UPDATE SKIP LOCKED` = Leader-Koordination ohne Konsens),
      Retry/Backoff exponentiell in SQL, Poison-Handling, `ResetCheckpointAsync` (Rebuild)
- [x] `GetLagAsync` + `ChangeFeedLagHealthCheck` (AspNetCore, portiert von v1)
- [x] Worker liest mit `'All'`-Scope (RLS-kompatibel)

**DoD erfüllt:** Gap-Test (lang offene Transaktion → höhere seq wird zurückgehalten,
nichts übersprungen, Ordnung bleibt), Wakeup-Latenz (NOTIFY schlägt 30s-Poll-Intervall
deutlich), Rebuild-Replay, Poison (2 Versuche → Skip + Failure-Eintrag, nachfolgende
Changes fließen), Lag 2→0, Leader-Lock-Test (gesperrter Checkpoint wird konfliktfrei
übersprungen), Uncommitted-Invisibility. 116 Tests grün.

## Phase 8 — Event-Log ✅ (2026-06-11)

ADRs: [013](adr/adr-013-business-event-log.md) · Ernte: Retention-Worker-Muster aus `Gdpr/RetentionWorker`

- [x] Tabelle `papuma.event` (txid, RLS inkl. FORCE, Index auf `(event_type,
      occurred_at)` für Retention) in `EnsureSchemaAsync`
- [x] `session.AppendAsync(...)` in der Session-Transaktion (Savepoint, NOTIFY,
      gemeinsame `correlationId`/`actorId`/`causationId`); Event-Typen im Metamodell
      (`Event<T>()` + `EventTypeBuilder`: Policies, Retention)
- [x] **Policies wirken auf den Payload in natürlicher Form**: Redact/DoNotTrack
      entfernen das Feld, Hash ersetzt den Wert; **Reference wird beim Build
      abgelehnt** (Events haben keinen Referenzort — concepts.md §12)
- [x] `EventFeedProcessor`: gleiche Engine-Garantien (txid-Snapshot, Stop-the-line,
      Poison, SKIP-LOCKED-Leader, NOTIFY-Wakeup, Lag); eigener Checkpoint-Raum via
      `event:`-Präfix — keine globale Ordnung über beide Feeds (concepts.md §13)
- [x] `EventRetention.PurgeExpiredAsync` (opt-in pro Typ via `.Retention(...)`)
- [x] Kein Upcasting für Events: bewusst keine `Upcast`-API am `EventTypeBuilder`,
      in XML-Docs begründet (unveränderliche Fakten, additive Regeln)

**DoD erfüllt:** Login-Szenario (Append + Patch atomar, geteilte correlationId über
beide Feeds), Uncommitted-Append-Rollback, Payload-Policy-Test (IP entfernt, Token
gehasht), Event-Processor-Zustellung in Ordnung + Deserialize, Retention purged nur
konfigurierte Typen, Reference-Ablehnung, nicht registrierter Event-Typ wirft.
123 Tests grün.

## Phase 9 — AspNetCore-Integration, DX und Doku

Ernte: `ScopeMiddleware`, `IScopeResolver`, Health-Check-Extensions

- [ ] `AddPapumaKernel(...)`-Bootstrap: DataSource, Metamodell-Build, EnsureSchema,
      Worker-Hosting
- [ ] Tenant-Resolution-Middleware portieren
- [ ] [Rezept Echtzeit-UI-Benachrichtigungen](recipes/realtime-ui-notifications.md)
      gegen die echte API verifizieren, Typnamen fixieren, Disclaimer entfernen
- [ ] README + Getting-Started für vNEXT; architecture.md gegen Implementierung
      abgleichen (Drift korrigieren)
- [ ] [concepts.md](concepts.md) ("Konzepte erklärt") als Tutorial-Rohstoff nutzen
      und um Erklärstücke aus Phase 8–10 ergänzen
- [ ] XML-Doku-Durchgang über die öffentliche API (AGENTS.md-Regeln)

**DoD:** Beispiel-App (Registrierungs-Szenario + Notifier) läuft end-to-end gegen
PG-18-Container.

## Phase 10 — v1-Rückbau

- [ ] v1-Quellverzeichnisse und -Tests entfernen (`Events/`, `Gdpr/` alt,
      `ChangeFeed/` alt, `Projections/` alt, `Schema/` alt — Tenancy-Originale erst
      jetzt, da vNEXT-Kopien etabliert)
- [ ] **Rename `.Next` → final**: Ordner/csproj `Papuma.Kernel.Next` →
      `Papuma.Kernel` (analog AspNetCore + Tests) per `git mv`; Pfade in `slnx` und
      CI anpassen. Kein Quellcode ändert sich — `RootNamespace` ist seit Phase 0
      `Papuma.Kernel`.
- [ ] Packaging reaktivieren: `IsPackable=true`, `PackageId Papuma.Kernel` /
      `Papuma.Kernel.AspNetCore`, Beschreibung/Tags auf vNEXT aktualisieren —
      das NuGet-Paket heißt unverändert `Papuma.Kernel`
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
| 1 | ~~`RETURNING OLD/NEW`-Verhalten~~ | ✅ Phase 2: verifiziert für UPDATE/DELETE (Spike-Tests); Interaktion mit `jsonb_set` folgt in Phase 5 |
| 2 | ~~PG-18-Image in CI~~ | ✅ Phase 0: `postgres:18-alpine` läuft lokal (Podman); CI-Lauf bestätigt sich beim ersten vnext-Push |
| 3 | ~~Diff-Pfad-Syntax für Arrays~~ | ✅ Phase 2: Arrays atomar, in ADR-004 nachgetragen |
| 4 | Performance der Diff-Engine bei großen Dokumenten | Benchmark in Phase 2; Limit-Empfehlung dokumentieren |
| 5 | `Reference`-Policy: eigener Sensitive Store nötig? | Start ohne (Referenz aufs Dokument); Bedarf nach Phase 8 neu bewerten (ADR-007) |
