# ADR-003: Atomarer Write-Pfad mit optimistischer Concurrency und RETURNING OLD/NEW

## Status

Accepted (2026-06-11)

## Kontext

Der naive Ablauf "altes Dokument laden → diffen → schreiben" hat zwei Probleme:

1. **Race Condition**: Zwischen Laden und Schreiben kann ein anderer Writer das Dokument
   ändern — das Diff wäre dann gegen einen veralteten Zustand gerechnet.
2. **Zwei Roundtrips** pro Save.

PostgreSQL 18 führt `RETURNING OLD/NEW` ein: ein einziges `UPDATE`/`DELETE` liefert
atomar den Zustand vor und nach der Änderung.

## Entscheidung

1. **Optimistische Concurrency ist eine Kernel-Invariante.** Jedes Dokument trägt eine
   `version` (bigint, startet bei 1). Updates und Deletes erfordern die erwartete
   Version; ein Treffer von 0 Zeilen wirft `ConcurrencyException`.
2. **Der Write-Pfad ist ein einziges Statement** pro Dokument:

   ```sql
   UPDATE papuma.document
   SET data = @data, version = version + 1, schema_version = @schemaVersion, updated_at = now()
   WHERE tenant_id = @tenantId AND document_type = @type AND id = @id
     AND version = @expectedVersion
   RETURNING old.data AS old_data, new.data AS new_data, new.version;
   ```

   Insert: `INSERT ... RETURNING new.version` (old ist NULL).
   Delete: `DELETE ... WHERE ... AND version = @expectedVersion RETURNING old.data`.

3. **Diff und ChangeRecord entstehen in derselben Transaktion**: Der Kernel diffed
   `old_data`/`new_data` in C#, wendet Policies an (ADR-007) und schreibt den
   `ChangeRecord` vor dem Commit. Dokument und Change sind nie inkonsistent.
4. Die API macht die erwartete Version explizit:

   ```csharp
   SaveResult<T> Save<T>(T document, long expectedVersion);   // 0 = Insert erwartet
   ```

   Ein "Last-Writer-Wins"-Modus existiert bewusst nicht.

## Konsequenzen

- Kein Zeitfenster zwischen Lesen und Schreiben; das Diff ist garantiert gegen den
  tatsächlich ersetzten Zustand gerechnet.
- Concurrency-Konflikte sind ein normaler, typisierter Fehlerfall, den Anwendungen
  behandeln müssen (Reload + Retry oder Fehler an den Aufrufer).
- **Erkennung ist Framework, Auflösung ist Anwendung.** Das deckt den
  Mehrbenutzer-Fall vollständig ab: Zwei Benutzer laden Version 5; der zweite Save mit
  `expectedVersion: 5` schlägt fehl — Lost Updates sind ausgeschlossen. Die `version`
  wird dazu durch die Anwendung geschleift (Frontend-Feld, ETag, API-Response). Die
  Version identifiziert den Dokumentinhalt eindeutig; ein separater Inhaltsvergleich
  wäre redundant.
- Die `ConcurrencyException` trägt erwartete und aktuelle Version. Damit kann die
  Anwendung über den Change Feed die zwischenzeitlichen Diffs laden (ADR-004) und
  präzise Konflikt-UIs bauen ("Feld X wurde zwischenzeitlich geändert") oder bei
  disjunkten Feldmengen selbst mergen — automatisches Mergen ist bewusst keine
  Kernel-Funktion (fachliche Entscheidung). Für konfliktarme Mehrbenutzer-Edits auf
  verschiedenen Feldern ist Patch das passende Primitiv (ADR-012).
- Die `version` im ChangeRecord ist lückenlos pro Dokument (Unique-Index
  `(tenant, type, id, version)` erzwingt das zusätzlich).
- **Neuanlage nach Delete setzt die Versionszählung fort** (Phase-2-Festlegung): Ein
  Insert startet bei `max(change.version) + 1` derselben Dokument-ID, nicht bei 1 —
  sonst würde die wiederverwendete ID mit der lückenlosen Change-Historie kollidieren.
  `expectedVersion: 0` behält die Semantik "Dokument existiert nicht".
- Bindung an PostgreSQL ≥ 18 (ADR-001) — gewollt.
