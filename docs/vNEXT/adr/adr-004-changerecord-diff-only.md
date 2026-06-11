# ADR-004: ChangeRecord speichert reversibles Diff, keine Snapshots

## Status

Accepted (2026-06-11)

## Kontext

Der ursprüngliche Entwurf (chat-1.md) sah `Before`, `After` **und** `Diff` im
ChangeRecord vor. Das verdreifacht den Storage pro Änderung — bei großen Aggregaten und
hoher Änderungsfrequenz wird der Feed schnell schwerer als die Dokumente selbst.
Gleichzeitig sind Before/After aus dem Diff rekonstruierbar, wenn das Diff beide
Wertseiten enthält.

Kandidaten für das Diff-Format:

- **RFC 6902 (JSON Patch)**: standardisiert, aber nur vorwärts anwendbar (kein `old`),
  und für Projektionen unhandlich ("op/path/value"-Listen statt Feldsicht).
- **Strukturiertes Feld-Diff** mit `old`/`new` pro Pfad: reversibel, direkt
  projektionstauglich (`WhenFieldChanged`), pro Feld policy-fähig.

## Entscheidung

1. Der ChangeRecord enthält **nur das Diff**, keine Before/After-Snapshots.
2. Format: **reversibles Feld-Diff** — eine JSONB-Map von JSON-Pfad auf Eintrag:

   ```json
   {
     "email":          { "old": null,    "new": "harry@example.com" },
     "address.city":   { "old": "Köln",  "new": "Bonn" },
     "roles[2]":       { "old": null,    "new": "admin" }
   }
   ```

   Bei `Insert` ist jedes `old` null, bei `Delete` jedes `new` null (das Delete-Diff
   enthält damit den letzten Zustand — vorbehaltlich Policies, ADR-007).
3. **Historische Zustände** entstehen durch Rückwärts-Anwenden der Diffs vom aktuellen
   Dokument aus (bzw. Vorwärts vom Insert). Das ist ein Audit-/Replay-Werkzeug, kein
   Hot Path.
4. Optionale **Snapshots** (z. B. alle n Versionen) sind eine spätere Optimierung, falls
   Rekonstruktion zu teuer wird — kein Bestandteil von vNEXT-Start.

## Konsequenzen

- Feed bleibt schlank; Storage wächst mit der Größe der Änderung, nicht des Aggregats.
- Rollback (ADR-008) ist trivial: Diff umdrehen, als Update anwenden.
- Policies wirken pro Feld auf genau eine Stelle (den Diff-Eintrag), nicht auf drei.
- Wer den vollen Zustand zu einem Zeitpunkt braucht, zahlt Replay-Kosten — akzeptiert,
  da der häufige Fall (aktueller Zustand) immer ein direkter Dokument-Load ist.
