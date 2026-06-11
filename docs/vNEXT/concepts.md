# Papuma vNEXT — Konzepte erklärt

Status: lebendes Dokument (Start 2026-06-11)

Die ADRs halten *Entscheidungen* fest — dieses Dokument erklärt die *Mechanismen
dahinter*, mit den Beispielen und Gedankengängen aus der Entwurfs- und
Implementierungsphase. Es ist bewusst erzählend geschrieben und dient als Rohstoff
für Tutorials und Onboarding. Jeder Abschnitt verlinkt sein ADR.

---

## 1. Warum ein einziges UPDATE das Race-Fenster schließt

→ [ADR-003](adr/adr-003-write-path-concurrency.md)

Der naive Weg, ein Diff zu berechnen, ist: altes Dokument laden, vergleichen,
schreiben. Zwischen Laden und Schreiben liegt aber ein Zeitfenster — ändert ein
anderer Writer das Dokument genau dann, ist das Diff gegen einen Zustand gerechnet,
der nie ersetzt wurde. Das Diff *lügt*.

PostgreSQL 18 löst das mit einem einzigen Statement:

```sql
UPDATE papuma.document
SET data = @data, version = version + 1
WHERE ... AND version = @expectedVersion
RETURNING old.data, new.data, new.version;
```

`old.data` ist garantiert exakt der Zustand, der ersetzt wurde — nicht "der Zustand
von vor ein paar Millisekunden". Es gibt kein Fenster, weil Lesen und Schreiben
derselbe atomare Vorgang sind. Deshalb ist PG ≥ 18 eine harte Anforderung (ADR-001)
und kein Optimierungsdetail.

Bonus: `new.data` kommt in der von Postgres *normalisierten* jsonb-Form zurück.
Das Diff wird gegen das gerechnet, was wirklich gespeichert ist — Serialisierungs-
Eigenheiten des Clients (Key-Reihenfolge, Zahlenformat) können es nicht verfälschen.

---

## 2. Der langsame und der schnelle Writer (die seq-Sichtbarkeitslücke)

→ [ADR-010](adr/adr-010-feed-consumption.md)

Das heimtückischste Problem eines gepollten Feeds: **Sequenznummern werden beim
INSERT vergeben, sichtbar werden Zeilen aber erst beim COMMIT — und diese
Reihenfolgen können sich kreuzen.**

Konkret: Transaktion A (der *langsame Writer*) beginnt zuerst und zieht `seq = 100`.
Transaktion B (der *schnelle Writer*) beginnt danach, zieht `seq = 101` — und
committet **zuerst**. Ein naiver Poller (`WHERE seq > checkpoint`) sieht jetzt 101,
verarbeitet sie, setzt seinen Checkpoint auf 101. Wenn A später committet, liegt
ihre 100 *hinter* dem Checkpoint — **sie wird nie verarbeitet. Still verloren.**

Die Lösung: Jeder ChangeRecord speichert seine Transaktions-ID (`txid xid8`), und
der Poller liest nur Changes, deren Transaktion *vor dem Horizont aller noch
laufenden Transaktionen* liegt:

```sql
WHERE seq > @checkpoint
  AND txid < pg_snapshot_xmin(pg_current_snapshot())
```

`pg_snapshot_xmin` ist die älteste noch offene Transaktion. Solange der langsame
Writer offen ist, gilt auch die schnellere 101 als "noch nicht stabil" und wird
zurückgehalten. Erst wenn A committet (oder abbricht), rückt der Horizont vor und
beide werden in seq-Reihenfolge geliefert. Kein Lag-Fenster, keine Heuristik —
MVCC selbst ist die Wahrheit.

Der Preis: Eine sehr lange offene Schreib-Transaktion hält den Feed-Fortschritt
*aller* Konsumenten auf. Das ist akzeptiert und beobachtbar (Lag-Metrik) — und ein
Grund mehr, warum Sessions kurze Transaktionen sein sollen.

---

## 3. NOTIFY ist der Wecker, Polling ist die Wahrheit

→ [ADR-010](adr/adr-010-feed-consumption.md)

LISTEN/NOTIFY allein wäre als Zustellmechanismus ungeeignet: Notifications sind
nicht persistent, Verbindungsabrisse verlieren sie lautlos. Polling allein wäre
träge (Latenz = Poll-Intervall) oder teuer (Dauerfeuer).

Die Kombination nimmt von beidem das Gute: Der Worker pollt — aber statt zwischen
den Zyklen blind zu schlafen, wartet er auf `NOTIFY papuma_changes` *oder* den
Timeout. Die Notification wird in `CommitAsync` gesendet und von Postgres atomar
mit dem Commit zugestellt — sie kann also nie auf Daten zeigen, die es noch nicht
gibt. Eine verpasste Notification kostet maximal ein Poll-Intervall Latenz, **nie
Daten**.

Im Test: Poll-Intervall bewusst auf 30 Sekunden gestellt — die Änderung kommt
trotzdem in Millisekunden an.

---

## 4. Stop-the-line: warum Ordnung vor Fortschritt geht

→ [ADR-009](adr/adr-009-projections-as-dumb-handlers.md)

Wenn ein Handler bei seq 105 wirft, gibt es zwei Schulen: *weiterlaufen und 105
später nachholen* (maximaler Durchsatz) oder *anhalten, bis 105 geklärt ist*
(strikte Ordnung). vNEXT hält an — der Checkpoint bleibt vor 105 stehen, Retry nach
exponentiellem Backoff.

Warum? Weil Handler auf die Ordnung *bauen dürfen sollen*: Eine SQL-Projektion, die
`Insert(105)` vor `Update(106)` desselben Dokuments braucht, dürfte sonst nie
einfach geschrieben werden. Out-of-order-Nachholen verlagert die Komplexität in
jeden einzelnen Handler — genau das Gegenteil von "Projektionen sind dumm".

Damit ein dauerhaft kaputter Change die Linie nicht ewig blockiert, gibt es das
**Poison-Ventil**: Nach `MaxAttempts` wird die Änderung übersprungen — aber der
Failure-Eintrag bleibt als permanenter Alarm-Record in `papuma.failure` stehen
(Betriebsthema, kein Datenverlust im Feed: der Change selbst ist ja noch da und
kann nach einem Fix per Rebuild nachgeholt werden).

---

## 5. Savepoints: warum ein Fehlschlag die Session nicht zerstört

→ Architektur §5, [ADR-003](adr/adr-003-write-path-concurrency.md)

Session = eine Transaktion (Unit of Work) hat ein Problem: In PostgreSQL gilt
"einmal Fehler, immer Fehler" — nach einem Constraint-Verstoß ist die *gesamte*
Transaktion abgebrochen, auch die drei erfolgreichen Writes davor wären verloren.

Deshalb läuft jeder Write unter einem **Savepoint**:

```text
SAVEPOINT papuma_write
  → Write ausführen
  → bei Erfolg:  RELEASE
  → bei Fehler:  ROLLBACK TO SAVEPOINT  (und Exception weiterwerfen)
```

Ein fehlgeschlagener Write (Concurrency-Konflikt, Unique-Verletzung, abgelehnter
Validator) rollt nur *sich selbst* zurück — frühere Writes bleiben intakt, die
Session bleibt nutzbar, der Aufrufer kann reagieren (Retry, anderen Wert, Abbruch).
Das versöhnt UoW-Atomarität mit den typisierten Fehlerfällen als *normalem*
Programmfluss.

Nebeneffekt für die Guards: Wenn der Schema-Guard *nach* dem bereits angewendeten
UPDATE wirft (er braucht ja `old.schema_version` aus dem RETURNING), macht der
Savepoint-Rollback das UPDATE ungeschehen. Prüfen-nach-Schreiben ist hier sicher,
weil Schreiben bis zum Commit nichts bedeutet.

---

## 6. Null ≠ Absent: das Detail, das Diffs reversibel macht

→ [ADR-004](adr/adr-004-changerecord-diff-only.md)

`{"email": null}` und ein Dokument *ohne* `email`-Feld sind in JSON verschiedene
Zustände. Ein Diff-Format, das beide gleich kodiert, kann nicht rückwärts angewendet
werden — soll `ApplyReverse` das Feld *entfernen* oder *auf null setzen*?

Die Lösung kostet nichts: Die Existenz wird über die **Anwesenheit der Schlüssel**
kodiert, der Wert darf legitim `null` sein.

```json
{ "email": { "new": "x@y.z" } }              ← Feld existierte vorher nicht
{ "email": { "old": null, "new": "x@y.z" } } ← Feld existierte, war null
```

Erst diese Unterscheidung macht die beiden Invarianten beweisbar, die die
Diff-Engine per Property-Tests garantiert: `Apply(before, diff) == after` und
`ApplyReverse(after, diff) == before`.

---

## 7. Warum Arrays atomar gedifft werden

→ [ADR-004](adr/adr-004-changerecord-diff-only.md)

Index-basierte Array-Diffs (`roles[2]: {old, new}`) sehen präzise aus, sind aber
eine Falle: Wird vorne ein Element eingefügt, "ändern" sich alle Indizes dahinter —
das Diff wird riesig und semantisch irreführend ("roles[5] geändert", obwohl nur
eingefügt wurde). Identitäts-basierte Diffs (LCS, Move-Erkennung) lösen das, kosten
aber genau die Komplexität, die Apply/Reverse fehleranfällig macht.

vNEXT wählt die langweilige, beweisbar korrekte Variante: **Unterscheiden sich
Arrays, gibt es einen Eintrag mit altem und neuem Gesamtarray.** Reversibel ohne
Sonderfälle, projektionstauglich ("roles hat sich geändert"), und Policies wirken
auf genau einen Pfad. Element-Granularität bleibt eine spätere Optimierung *hinter*
demselben Wire-Format.

Gleiche Logik beim **Typwechsel auf einem Pfad**: Wird aus dem Objekt `address` ein
String, wird nicht rekursiert, sondern ein atomarer Eintrag mit dem Gesamtobjekt
als `old` erzeugt — Rekursion über einen Typwechsel hinweg würde die Reversibilität
brechen.

---

## 8. Warum Rollback über redactete Felder scheitern *muss*

→ [ADR-007](adr/adr-007-privacy-policies.md), [ADR-008](adr/adr-008-rollback-is-update.md)

`RollbackAsync` rekonstruiert alte Zustände durch Rückwärts-Anwenden der Diffs.
Ein redacteter Eintrag (`{"changed": true}`) enthält aber *keinen Wert* — das ist
sein Zweck. Der Kernel könnte raten (alten Wert leer lassen? aktuellen behalten?),
aber jede Variante wäre **stillschweigend falscher Zustand**.

Deshalb: typisierter Fehler (`RollbackNotPossibleException` mit Pfad und Policy-Art)
statt Bauchgefühl. Wer das Feld wiederherstellen will, tut es explizit über die
Referenzquelle — auditierbar statt magisch.

Eine Konsequenz, die erst beim Implementieren sichtbar wurde: Auch **Insert- und
Delete-Diffs** sensibler Felder sind redacted (sonst stünde der letzte Wert im
Klartext im Feed). Ein Rollback über eine Delete/Recreate-Kette eines Dokuments
mit sensiblen Feldern scheitert daher ebenfalls — korrekt, denn der Kernel kennt
die alten Geheimnisse schlicht nicht.

---

## 9. Insert nach Delete: warum Versionen weiterzählen

→ [ADR-003](adr/adr-003-write-path-concurrency.md)

Wird ein Dokument gelöscht und seine ID später wiederverwendet, dürfte ein naiver
Insert wieder bei Version 1 beginnen — und würde mit der Change-Historie
kollidieren: Dort existieren bereits Records für Version 1 (Insert), 2 (Delete),
und der Unique-Index `(scope, tenant, type, id, version)` würde feuern.

Deshalb setzt der Insert bei `max(change.version) + 1` auf: Insert → Delete →
Insert ergibt die Versionen 1, 2, **3**. Die Historie pro Dokument-ID bleibt
lückenlos und chronologisch lesbar — inklusive der Wiedergeburt. Und genau diese
durchgehende Kette ist es, die `RollbackAsync` sogar über Delete/Recreate hinweg
funktionieren lässt: Insert-Diff rückwärts = leeres Objekt, Delete-Diff rückwärts =
der alte Zustand.

`expectedVersion: 0` behält dabei seine Semantik "ich erwarte, dass das Dokument
nicht existiert" — der Aufrufer muss von der Vorgeschichte nichts wissen.

---

## 10. Warum Patch kein Load braucht — und wann LWW richtig ist

→ [ADR-012](adr/adr-012-partial-updates.md)

Ein Patch lädt das Dokument nicht — weder der Aufrufer noch der Kernel intern.
Der "Read" passiert im UPDATE selbst: `jsonb_set` wendet die Änderung auf den
Zustand *in Postgres* an, `RETURNING old/new` liefert das Diff-Material. Ein
Roundtrip, kein Fenster.

Das erlaubt eine differenzierte Concurrency-Haltung:

- **Ohne `expectedVersion`** ist ein Patch bewusstes *Field-Level Last-Writer-Wins*.
  Zwei Admins, die gleichzeitig `displayName` und `phone` ändern, haben keinen
  echten Konflikt — die Versionsnummer serialisiert ihre Patches trotzdem sauber
  (2, 3), und das Diff jeder Änderung ist korrekt.
- **Mit `expectedVersion`** wird der Patch zum Read-Modify-Write — nötig, sobald
  der neue Wert von zuvor *gelesenem* Zustand abhängt.
- **`Increment`** braucht beides nicht: Die Abhängigkeit vom aktuellen Wert wird
  als SQL-Ausdruck im Statement gelöst — atomar per Konstruktion.

Der volle `Save` verlangt dagegen *immer* `expectedVersion`: Dort ist das ganze
Dokument der behauptete Stand, und ein ungeprüftes Überschreiben wäre genau das
Lost-Update-Problem aus der Mehrbenutzer-Diskussion (ADR-003).

---

## 11. Leader-Koordination ohne Konsens-Protokoll

→ [ADR-010](adr/adr-010-feed-consumption.md)

Laufen mehrere Prozesse mit demselben Handler (Scale-out, Deployment-Überlappung),
braucht es genau einen aktiven Verarbeiter pro Handler — aber kein ZooKeeper, kein
Lease-Protokoll. Die Checkpoint-*Zeile* selbst ist der Lock:

```sql
SELECT last_seq FROM papuma.checkpoint
WHERE handler_name = @name
FOR UPDATE SKIP LOCKED
```

Hält ein anderer Prozess die Zeile, kommt `SKIP LOCKED` sofort mit leerem Ergebnis
zurück — der Prozess überspringt den Handler in diesem Zyklus, ohne zu blockieren.
Stirbt der Halter, gibt Postgres den Lock mit dessen Transaktion automatisch frei.
Failover ist damit ein Nebeneffekt der Transaktionssemantik, kein eigenes System.

---

## 12. Warum Policies auf Event-Payloads anders wirken als auf Diffs

→ [ADR-013](adr/adr-013-business-event-log.md), [ADR-007](adr/adr-007-privacy-policies.md)

Im Change Feed transformieren Policies *Diff-Einträge* — `{"changed": true}` statt
Werten. Bei Events geht das nicht: Der Payload ist das Faktum selbst, und Konsumenten
wollen ihn als `UserLoggedIn` *deserialisieren*. Ein `{"changed": true}` mitten im
Payload würde die Form zerstören.

Deshalb behalten Event-Payloads ihre natürliche Gestalt, die Policies wirken auf die
Felder selbst:

- **Redact / DoNotTrack** → Feld wird vor dem Speichern *entfernt* (beim
  Deserialisieren kommt der Default zurück — Konsumenten sehen geschützte Werte nie)
- **Hash** → Wert wird durch den SHA-256-Hex-String ersetzt (vergleichbar ohne Inhalt)
- **Reference** → **beim Model-Build abgelehnt.** Im Diff zeigt eine Referenz auf den
  Wert im Dokument ("die Wahrheit liegt woanders"). Ein Event *ist* aber selbst der
  Record — es gibt keinen Ort, auf den die Referenz zeigen könnte. Lieber ein lauter
  Fehler beim Start als eine Referenz ins Nichts.

---

## 13. Zwei Feeds, keine globale Ordnung — und warum das reicht

→ [ADR-013](adr/adr-013-business-event-log.md)

Change Feed und Event-Log haben getrennte Sequenzen und getrennte Checkpoint-Räume
(Event-Handler werden intern mit `event:`-Präfix in derselben Checkpoint-Tabelle
geführt). Ein Handler, der beides konsumiert, bekommt **keine garantierte Ordnung
zwischen den Feeds** — bewusst: Eine vereinheitlichte Sequenz (v1s `kind`-Spalte im
unified Feed) hätte beide Welten aneinander gekettet.

Was stattdessen die Verbindung herstellt, ist die **`correlationId` der Session**:
Das Login-Faktum (`Append(UserLoggedIn)`) und der Zustands-Patch (`lastLoginAt`)
committen atomar in einer Transaktion und tragen dieselbe Correlation — wer
Zusammenhänge braucht, korreliert statt zu ordnen. Und weil beide Tabellen dieselbe
`txid`-Mechanik nutzen, gilt die Gap-Garantie aus Abschnitt 2 in beiden Feeds.

---

*Pflegehinweis: Neue Erklärstücke aus späteren Phasen hier ergänzen — dieses
Dokument ist der Sammelpunkt für das "Warum hinter dem Wie" und Rohstoff für die
Tutorials (Phase 9).*
