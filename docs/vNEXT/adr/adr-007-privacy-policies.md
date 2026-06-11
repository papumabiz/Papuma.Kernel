# ADR-007: Datenschutz-Policies — Attribute als Default, Fluent als Override

## Status

Accepted (2026-06-11)

## Kontext

Da der Kernel das C#-Modell kennt (Typen, Properties, Attribute), kann er
Datenschutz-Regeln automatisch auf den Change Feed anwenden — bevor personenbezogene
Daten jemals einen unveränderlichen Feed erreichen. Das ist das Alleinstellungsmerkmal
von vNEXT gegenüber Outbox- und Event-Sourcing-Systemen, die dieses Problem Jahre später
schmerzhaft einholt.

Die vNEXT-Diskussion endete bei "nur Fluent-Konfiguration" (weil Policies
organisationsabhängig sind). Dagegen spricht: Das Modell ist der Ort der Wahrheit — wer
die Klasse liest, soll sehen, dass `Email` sensibel ist.

Vorarbeit aus v1, die konzeptionell übernommen wird:
[adr-2026-06-sensitive-data-reference-pattern.md](../../analyses/adr-2026-06-sensitive-data-reference-pattern.md)
(Hybrid-Modell: Referenzen im Feed, versionierter Sensitive Data Store, explizite
opt-in-Auflösung statt unsichtbarer Magie).

## Entscheidung

1. **Policy-Katalog** (Wirkung auf den Diff-Eintrag eines Feldes, vgl. ADR-004):

   | Policy       | Attribut          | Diff-Eintrag                          |
   |--------------|-------------------|---------------------------------------|
   | `Track`      | — (Default)       | `{ "old": ..., "new": ... }`          |
   | `Redact`     | `[SensitiveData]` | `{ "changed": true }`                 |
   | `Reference`  | `[TrackReference]`| `{ "ref": "User/123/email" }`         |
   | `Hash`       | `[TrackHash]`     | `{ "changed": true, "hash": "..." }`  |
   | `DoNotTrack` | `[DoNotTrack]`    | Feld erscheint nicht im Diff          |

2. **Zwei Quellen, klare Priorität**: Attribute an der Klasse setzen den Default;
   Fluent-Konfiguration beim Store-Setup überschreibt pro Organisation/Deployment:

   ```csharp
   builder.For<User>()
       .Property(x => x.Email).StoreAsReference()   // Override: Redact → Reference
       .Property(x => x.LastLoginIp).DoNotTrack();
   ```

3. **Policies wirken beim Erzeugen des Diffs**, in derselben Transaktion wie der Save
   (ADR-003). Es gibt keinen nachgelagerten Scrubbing-Prozess für neue Changes.
4. **Referenzen lösen nie automatisch auf.** Change Handler, die den Wert brauchen,
   lösen explizit über eine Resolver-API auf (opt-in) — gegen den aktuellen
   Dokumentzustand bzw. den Sensitive Data Store. Wird das Dokument DSGVO-gelöscht,
   laufen Referenzen ins Leere; der Feed bleibt frei von Inhalten.
5. **Delete-Diffs respektieren Policies**: Auch das letzte `old` eines sensiblen Feldes
   erscheint nur als `changed`/`ref`, nie als Klartext.
6. Das Metamodell (inkl. Policies) wird **einmal beim Start** gebaut (Reflection,
   später optional Source Generator) — Laufzeitkosten pro Save sind Lookups, keine
   Reflection.

## Konsequenzen

- DSGVO-Löschung = Dokument löschen; der Feed muss nicht angefasst werden.
- Die Klasse dokumentiert die Default-Sensitivität; Compliance-Abweichungen pro
  Organisation sind ohne Recompile möglich (Fluent).
- Handler, die sensible Werte brauchen, sind im Code als solche erkennbar
  (expliziter Resolver-Aufruf) — auditierbar statt magisch.
- Der Sensitive Data Store (versionierte Auslagerung) wird nur gebraucht, wenn
  historische sensible Werte über den Dokument-Lebenszyklus hinaus benötigt werden;
  für den Start genügt das Reference-Pattern gegen das Dokument selbst.
