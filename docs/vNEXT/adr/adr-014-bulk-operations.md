# ADR-014: Bulk-Operationen als set-basierter Patch

## Status

Accepted (2026-06-11)

## Kontext

Anwendungen brauchen Änderungen über mehrere Dokumente: "alle User mit Status X
archivieren", "diese Liste von IDs bearbeiten". PostgreSQLs `RETURNING OLD/NEW` ist
set-basiert — ein `UPDATE ... WHERE` liefert **eine Zeile pro betroffenem Dokument**
mit altem und neuem Zustand. Der Phase-2-Spike-Befund (ADR-003) trägt also auch für
Mengen: ein Statement, atomar, Diff-Material für jedes Dokument.

Gleichzeitig droht hier die Hintertür zum Query-DSL, das vNEXT bewusst nicht sein will
(ADR-006/009): "Update mit beliebigem WHERE" wäre der Anfang eines LINQ-Providers.

## Entscheidung

1. **Bulk = Patch, nie Save.** Bulk-Operationen verwenden ausschließlich den
   Patch-Katalog aus ADR-012 (`Set` / `Remove` / `Increment`) — derselbe Patch über
   N Dokumente. Ein "Bulk-Save" ganzer Dokumente existiert nicht.
2. **Zwei Selektionsformen, keine dritte:**

   ```csharp
   // a) Prädikat auf deklarierten Metamodell-Keys (ADR-006)
   await session.PatchWhereAsync<User>(
       where: w => w.Key(x => x.Status, "inactive"),
       patch: p => p.Set(x => x.Status, "archived"));

   // b) Explizite ID-Liste (WHERE id = ANY(@ids))
   await session.PatchManyAsync<User>(ids, p => p.Set(x => x.Status, "archived"));
   ```

   Prädikate sind auf **deklarierte Keys** beschränkt (Gleichheit, ggf. Key-Listen) —
   genau die Felder, die ohnehin Expression-Indizes tragen. Komplexere Selektionen
   ermittelt die Anwendung selbst (Projektion, SQL) und nutzt Form (b). Ein freies
   WHERE-DSL gibt es bewusst nicht.
3. **Delete analog**: `DeleteWhereAsync` / `DeleteManyAsync` über
   `DELETE ... RETURNING old.data` — gleiche Mechanik, Delete-Diffs pro Dokument.
4. **Ein ChangeRecord pro Dokument**, nicht pro Statement. Konsumenten (ADR-009)
   merken nichts Besonderes; die lückenlose Versionierung pro Dokument bleibt intakt
   (jede Trefferzeile bumpt ihre eigene `version`). Eine gemeinsame `correlationId`
   in den Metadaten verbindet die Records einer Bulk-Operation; die Change-Inserts
   erfolgen gebündelt in derselben Transaktion.
5. **Kein `expectedVersion`.** Bulk ist per Definition zustandsbasiert: Prädikat und
   Änderung wirken im selben Statement auf den aktuellen Zustand — es gibt kein
   Read-Modify-Write-Fenster, die Operation ist in sich konsistent. Parallele
   optimistische Writer laufen anschließend korrekt in die `ConcurrencyException`
   (ihre erwartete Version wurde gebumpt).
6. **Schema-Guard wie ADR-012**: Trifft der Bulk-Patch Dokumente mit veralteter
   `schema_version`, deren gepatchte Pfade upcasting-betroffen sind, schlägt die
   Operation typisiert fehl — keine stille Korruption alter Dokumente.
7. **Policies und Diff-Pipeline unverändert**: Jedes betroffene Dokument durchläuft
   dieselbe Diff-Erzeugung und Policy-Anwendung wie ein Einzel-Patch (ADR-007).

## Konsequenzen

- Massenänderungen sind ein einziger Roundtrip statt N — und atomar: entweder alle
  Treffer samt ChangeRecords oder nichts.
- Key-Prädikate sind automatisch indexgestützt (Synergie mit ADR-006).
- Große Treffermengen bedeuten eine große Transaktion und einen Feed-Schwall —
  Batch-Begrenzung (z. B. ID-Listen chunken) ist Anwendungsentscheidung und wird
  dokumentiert. Sehr lange Bulk-Transaktionen verzögern außerdem den Feed-Fortschritt
  aller Konsumenten (Snapshot-Lesen, ADR-010).
- Validierung pro Dokument (ADR-012 Punkt 5) ist bei großen Mengen teuer; Bulk-Patches
  auf validierte Typen deserialisieren jede Trefferzeile — bewusster Trade-off des
  Aufrufers.
- Umsetzung in Phase 5 zusammen mit dem Einzel-Patch (gemeinsame SQL-Generierung).
