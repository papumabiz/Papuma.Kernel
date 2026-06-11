# ADR-008: Rollback ist ein Update mit Metadata, kein eigener Operationstyp

## Status

Accepted (2026-06-11)

## Kontext

Die Idee "Events: Insert / Update / Delete / Rollback" lag nahe. Aber: Ein Rollback auf
Version n ist technisch eine Markierung, fachlich jedoch schlicht eine Zustandsänderung.
Projektionen interessiert nur der State-Übergang — ein vierter Operationstyp würde jeden
Handler zwingen, einen Sonderfall zu behandeln, der keiner ist.

## Entscheidung

1. `ChangeOperation` bleibt **Insert / Update / Delete**.
2. Rollback ist eine Kernel-API, die den Zielzustand rekonstruiert (Diffs rückwärts
   anwenden, ADR-004) und als **normales Update** speichert — mit Metadata:

   ```csharp
   await session.RollbackAsync<User>(id, toVersion: 3, expectedVersion: 7);
   ```

   erzeugt einen ChangeRecord mit `version = 8` und

   ```json
   { "isRollback": true, "restoredVersion": 3 }
   ```

3. Die Versionshistorie ist **append-only**: Ein Rollback löscht keine Changes, er fügt
   einen hinzu. Version 8 hat denselben Inhalt wie Version 3 — das Log bleibt lückenlos
   und auditierbar.
4. Handler, die Rollbacks gesondert behandeln wollen, lesen `metadata.isRollback` —
   müssen aber nicht.

## Konsequenzen

- Projektionen bleiben dumm: drei Operationen, fertig.
- Audit bleibt vollständig: Wer wann worauf zurückgesetzt hat, steht im Feed.
- Rollback über Schema-Versionen hinweg läuft durch die Upcaster-Pipeline (ADR-005),
  bevor gespeichert wird — der rekonstruierte Zustand wird immer im aktuellen Schema
  geschrieben.
- Policy-redactete Felder (ADR-007) sind aus Diffs nicht rekonstruierbar; ein Rollback
  stellt für solche Felder den Wert aus dem referenzierten Speicherort wieder her oder
  schlägt typisiert fehl — stillschweigend falsche Werte gibt es nicht.
