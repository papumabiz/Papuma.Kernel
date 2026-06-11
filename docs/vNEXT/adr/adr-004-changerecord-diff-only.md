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
     "roles":          { "old": ["user"], "new": ["user", "admin"] }
   }
   ```

   Bei `Insert` fehlt jede `old`-Seite, bei `Delete` jede `new`-Seite (das Delete-Diff
   enthält damit den letzten Zustand — vorbehaltlich Policies, ADR-007).

   **Festgelegt in Phase 2 (2026-06-11):**
   - **Null ≠ Absent**: Ob ein Feld existierte, wird über die *Anwesenheit* der
     `old`-/`new`-Schlüssel kodiert (Schlüssel weggelassen = Feld existierte nicht);
     der *Wert* darf legitim JSON-`null` sein. "Feld hinzugefügt" und "Feld von null
     geändert" sind damit unterscheidbar — Voraussetzung für Reversibilität.
   - **Arrays sind atomare Blattwerte**: Unterscheiden sich Arrays, entsteht genau ein
     Eintrag auf dem Array-Pfad mit altem und neuem Gesamtarray. Kein Index-Diffing
     (`roles[2]`) — das vermeidet die Mehrdeutigkeit verschobener Indizes und hält
     Apply/Reverse trivial korrekt. Element-Granularität ist eine spätere Optimierung.
   - **Verschachtelte Objekte** werden rekursiv gedifft (`address.city`); Pfade sind
     punkt-separiert. Schlüssel, die selbst '.' enthalten, werden nicht unterstützt
     (bei serialisierten POCOs nicht erreichbar).
3. **Historische Zustände** entstehen durch Rückwärts-Anwenden der Diffs vom aktuellen
   Dokument aus (bzw. Vorwärts vom Insert). Das ist ein Audit-/Replay-Werkzeug, kein
   Hot Path.
4. Optionale **Snapshots** (z. B. alle n Versionen) sind eine spätere Optimierung, falls
   Rekonstruktion zu teuer wird — kein Bestandteil von vNEXT-Start.

## Verworfene Alternative: 3rd-Party-Diff-Libraries

Geprüft (2026-06-11): `SystemTextJson.JsonDiffPatch` (jsondiffpatch-Delta-Format auf
`JsonNode`, reversibel, LCS-Array-Diffing), `JsonPatch.Net`/json-everything (RFC 6902)
und `JsonDiffPatch.Net` (Newtonsoft). Entscheidung: **eigene Engine**, denn das
Wire-Format ist hier das Produkt, nicht das Werkzeug:

- **RFC 6902 ist nicht reversibel** (`replace` trägt keinen alten Wert) — disqualifiziert
  für ADR-004.
- **jsondiffpatch-Deltas** sind reversibel, aber verschachtelt und mit Magic-Markern
  kodiert — die flachen Punkt-Pfade gingen verloren, auf denen Policy-Anwendung
  (ADR-007, pro Feld genau ein Eintrag), `WhenFieldChanged`-Filter und
  SQL-Abfragbarkeit des Diffs (`diff ? 'email'`) beruhen. Das LCS-Array-Diffing löst
  zudem genau die Komplexität, die wir mit "Arrays atomar" bewusst ausgeschlossen haben.
- Die eigene Engine ist ~150 Zeilen mit property-getesteten Roundtrip-Invarianten und
  ohne Paketabhängigkeit (AGENTS.md: BCL bevorzugen).

Falls Element-Granularität für Arrays später nötig wird, ist
`SystemTextJson.JsonDiffPatch` der erste Kandidat — dann als interner Algorithmus
hinter dem bestehenden Wire-Format, nicht als Formatwechsel.

## Konsequenzen

- Feed bleibt schlank; Storage wächst mit der Größe der Änderung, nicht des Aggregats.
- Rollback (ADR-008) ist trivial: Diff umdrehen, als Update anwenden.
- Policies wirken pro Feld auf genau eine Stelle (den Diff-Eintrag), nicht auf drei.
- Wer den vollen Zustand zu einem Zeitpunkt braucht, zahlt Replay-Kosten — akzeptiert,
  da der häufige Fall (aktueller Zustand) immer ein direkter Dokument-Load ist.
