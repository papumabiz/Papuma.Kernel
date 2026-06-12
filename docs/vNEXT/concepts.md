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

**Wer zahlt hier was?** Die Writer **nichts** — der schnelle Writer wartet nie auf
den langsamen, beide committen unabhängig mit vollem Durchsatz; es gibt keine
Schlange und kein Lock zwischen ihnen. Bezahlt wird ausschließlich in
*Konsumenten-Latenz*, gedeckelt durch die längste gleichzeitig offene
**Schreib**-Transaktion. Bei vielen Nutzern mit vielen kurzen Sessions rückt der
Horizont kontinuierlich vor (steigendes Volumen macht es eher besser);
Nur-Lese-Sessions halten ihn gar nicht auf, weil Postgres Transaktions-IDs erst
beim ersten Write vergibt. Der eine Risikofall bleibt die einzelne lange offene
Schreib-Session (z. B. über User-Denkzeit hinweg) — genau dafür ist der
Lag-Health-Check da.

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

**Wer zahlt hier was?** Es stoppt ausschließlich die Linie des *einen*
fehlschlagenden Handlers — Writer und andere Handler (eigene Checkpoints) sind
nicht betroffen. Die Kosten sind Latenz, nicht Durchsatz: Während des Backoffs
wächst der Lag dieses Handlers um `Schreibrate × Backoff-Dauer`; danach holt er in
Batches auf, was deutlich schneller geht als Echtzeit-Konsum. Voraussetzung ist
Aufhol-Headroom — ein Handler, der der Schreibrate dauerhaft nicht folgen kann,
hat wachsenden Lag mit oder ohne Stop-the-line (das ist Grenze 2 aus §14, nicht
der Wartemechanismus).

**Wo Writer wirklich interagieren** (der Vollständigkeit halber): nur am *Hot
Document*. Zwei gleichzeitige Writes auf dieselbe Zeile serialisiert Postgres am
Row-Lock für die (kurze) Dauer der ersten Transaktion, dann greift die
Versionsprüfung → `ConcurrencyException` → App-Retry. Verschiedene Dokumente
interagieren gar nicht: 10.000 Nutzer auf 10.000 Dokumenten skalieren linear;
10.000 Nutzer auf *einem* globalen Zähler serialisieren an der Zeile — ein
Modellierungsthema (Zähler sharden), kein Engine-Problem.

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

## 14. Das Skalierungsmodell der Feed-Engines — Grenzen und Auswege

→ [ADR-009](adr/adr-009-projections-as-dumb-handlers.md), [ADR-010](adr/adr-010-feed-consumption.md)

Pro Prozess läuft **ein** `ChangeFeedProcessor` und **ein** `EventFeedProcessor`;
registriert werden *Handler*, nicht Prozessoren. Parallelität entsteht über
App-Instanzen — und dort gilt: `FOR UPDATE SKIP LOCKED` macht Scale-out zu
**Failover, nicht Throughput**. Pro Handler konsumiert immer genau eine Instanz,
weil die strikte seq-Ordnung genau einen Konsumenten verlangt (wie Kafka mit einer
Partition).

Die drei echten Grenzen, in der Reihenfolge, in der man sie trifft:

1. **Der langsamste Handler bestimmt die Zykluslatenz.** Handler laufen pro Zyklus
   sequenziell; sie sind daten-entkoppelt (eigene Checkpoints), aber latenz-gekoppelt.
   Bis ~10–20 zügige Handler irrelevant; ein Handler mit externem HTTP-Call zieht
   alle in die Latenz.
2. **Durchsatz pro Handler ist single-threaded** — die architektonische Decke.
   Ein projektierender Handler (1 SQL-Write pro Change) schafft realistisch einige
   hundert Changes/s. Schreibt die Anwendung dauerhaft schneller, wächst der Lag
   unbegrenzt; mehr Instanzen helfen nicht.
3. **Lese-Amplifikation**: Jeder Handler liest den vollen Feed (kein Typ-Filter im
   SQL) — billig dank PK-Range-Scan ab Checkpoint, aber bei Volumen × Handler-Zahl
   messbar.

**Kein Problem**: NOTIFY-Stürme (der Prozessor arbeitet ohnehin bis "leer"),
Connections (1–2 + LISTEN pro Prozessor), die zwei Prozessoren nebeneinander
(getrennte Tabellen und Checkpoint-Räume).

**Designhaltung**: Korrektheit + Beobachtbarkeit vor Durchsatz. Pull-basiert gibt es
keinen Backpressure-Kollaps — nur wachsenden Lag, und genau den machen
`GetLagAsync` + Health-Check sichtbar, lange bevor etwas kippt.

**Die geplanten Auswege** (bewusst aufgeschoben, bis Lag-Metriken den Bedarf zeigen):
Handler-Parallelisierung im Zyklus (`Task.WhenAll`, löst Grenze 1 — klein, da jeder
Handler eigene Connection/Checkpoint hat), SQL-seitiger `document_type`-Filter pro
Handler (löst Grenze 3), und als echtes Feature Handler-Sharding per
`document_id`-Hash (löst Grenze 2 und erhält die fachlich relevante Ordnung *pro
Dokument* bei N parallelen Konsumenten).

---

## 15. Observability ohne Vendor: warum BCL-Primitives reichen — und der Link-Trick

→ Phase 11, [observability.md](observability.md)

In .NET ist "OpenTelemetry oder etwas Besseres?" eine falsche Dichotomie: `Meter`
und `ActivitySource` aus der BCL *sind* die vendor-neutralen Quellen, und OTel,
Prometheus oder `dotnet-counters` sind austauschbare Konsumenten. Der Kernel nimmt
deshalb null Abhängigkeiten und instrumentiert direkt — `AddMeter("Papuma.Kernel")`
genügt der Anwendung.

Zwei Details, die nicht offensichtlich sind:

**Der Lag-Gauge ist ein Cache, kein Live-Query.** Observable Gauges werden synchron
abgefragt, der Lag erfordert aber eine DB-Abfrage. Deshalb füttert `GetLagAsync` einen
Cache, den die Run-Loop in ihren Idle-Momenten auffrischt — Gauge-Frische ≈
Poll-Intervall. Und weil Observable Gauges nicht einzeln deregistrierbar sind, hält
jeder Prozessor einen *eigenen* Meter (gleicher Name!), der mit ihm disposed wird.

**Handler-Spans verlinken statt zu erben.** Die Session schreibt den aktiven
`traceparent` in die Change-Metadata; der Handler-Span nimmt ihn als Span-**Link**,
nicht als Parent. Bewusst: Ein Parent würde behaupten, die Feed-Verarbeitung sei
Teil der Request-Latenz — ist sie nicht, sie ist asynchrone Batch-Arbeit, womöglich
Minuten später (Backoff!). Der Link sagt korrekt "wurde verursacht von", und der
Trace-Viewer beantwortet trotzdem per Klick, welcher Request eine Projektion
ausgelöst hat. Datenschutz-Bonus: Durch die Policy-Bereinigung der Diffs ist die
gesamte Observability-Pipeline PII-arm by design.

---

## 16. Lesen mit Garantie: Session-Load, SQL-Views und Projektionen

→ [ADR-002](adr/adr-002-document-as-truth.md), [ADR-005](adr/adr-005-schema-evolution.md), [ADR-006](adr/adr-006-keys-and-constraints.md)

Projektionen sind asynchron — aber **der Document Store ist die Wahrheit**, und
`LoadAsync`/`LoadByKeyAsync` lesen ihn direkt und transaktional konsistent. Der
Klassiker "Passwortänderung muss sofort abrufbar sein" ist deshalb der eingebaute
Normalfall: Login-Check via `LoadByKeyAsync` liest den Stand *jetzt*, ohne Feed,
ohne Lag, indexgestützt über den deklarierten Key.

**SQL-Views über den JSONB-Store** sind als zusätzliche "Lese-Linse" legitim — genau
dafür liegt die Wahrheit als JSONB *in Postgres*. Eine View ist garantiert aktuell
(gleicher MVCC-Snapshot), braucht keine Sync-Maschinerie und verletzt kein ADR.
Vier Caveats gehören dazu:

1. **Nur lesen.** Writes gehen immer durch die Session (Diffs, Policies, Concurrency).
2. **`security_invoker = on`** (PG ≥ 15) ist Pflicht — sonst wertet Postgres die
   RLS-Policies gegen den View-Owner statt den Aufrufer aus und die Tenant-Isolation
   ist still ausgehebelt.
3. **Views sehen die gespeicherte Form, nicht die upgecastete.** Die Upcaster-Pipeline
   läuft im Kernel, nicht in SQL — nach einem Rename liegen dank Lazy-Upcasting noch
   Alt-Dokumente in alter Form da (`COALESCE(data->>'neu', data->>'alt')` als
   Übergang, oder Views auf schema-stabile Felder beschränken).
4. **Policies wirken nicht** — der Store enthält Klartext; Grants auf Views, die
   sensible Felder exponieren, entsprechend eng halten.

Die Entscheidungsmatrix:

| Bedarf | Werkzeug | Konsistenz |
|---|---|---|
| Strong-consistency-Read im Code (Login, Geschäftslogik) | `LoadAsync` / `LoadByKeyAsync` | sofort |
| Ad-hoc-SQL, Reporting, BI auf aktuellen Daten | View (mit den 4 Caveats) | sofort |
| Schwere Read-Models, Aggregationen, externe Ziele | Projektion via Handler | eventual (Lag beobachtbar) |

Materialized Views sind die schlechteste der Welten: Sie holen die Staleness zurück
(`REFRESH`-Zyklus), ohne die Freiheit einer echten Projektion zu bieten.

---

## 17. Der begrenzte Zähler: Lagerbestand ohne Überverkauf

→ [ADR-012](adr/adr-012-partial-updates.md), §4 (Hot Document), §10 (Increment)

Das Shop-Problem "nur N Stück auf Lager, niemals überverkaufen" hat zwei korrekte
Lösungen — und eine klare Empfehlung:

**Optimistisch (Load + Check + Save mit `expectedVersion`)** kann nie überverkaufen:
Die Versionsprüfung macht Read-Check-Write effektiv atomar; der Verlierer bekommt
die `ConcurrencyException` und versucht es erneut. Unter Flash-Sale-Last wird das
aber zum Retry-Karussell am Hot Document — korrekt, aber verschwenderisch.

**Der atomare bedingte Dekrement** komponiert zwei vorhandene Primitive:

```csharp
m.Document<Inventory>(d => d.Validate(inv =>
{
    if (inv.Stock < 0) throw new OutOfStockException(inv.Id);
}));

await session.PatchAsync<Inventory>(skuId, p => p.Increment(x => x.Stock, -1));
// wirft OutOfStockException, wenn der Bestand negativ würde — nichts geschrieben
```

`Increment` rechnet im Statement auf dem aktuellen Wert (kein Konfliktfenster, kein
`expectedVersion`), konkurrierende Käufer serialisiert Postgres kurz am Row-Lock,
und der Validator prüft das *gespeicherte Ergebnis* vor dem Commit — wird der
Bestand negativ, rollt der Savepoint das UPDATE zurück. Alle Käufer bis Bestand 0
gehen ohne einen einzigen Retry durch, danach wird typisiert abgelehnt. ADR-012 hat
"bedingte Patches" als Katalog-Feature abgelehnt — diese Komposition ist der
sanktionierte Weg zu bedingter Schreibsemantik.

Modellierung: Bestand als **eigenes kleines Dokument** pro SKU (entkoppelt
Content-Pflege von Bestandsbewegungen — Field-Level-LWW hin oder her, die Historien
bleiben sauber getrennt), Storno als `Increment(+1)`-Kompensation. Gratis-Bonus:
Der Change Feed des Inventory-Dokuments ist ein lückenloses **Bestands-Ledger**
(`stock: {old: 5, new: 4}` mit `correlationId` zur Bestellung). Für Extremfälle
(zehntausende Käufer auf *einer* SKU) wird die Zeilen-Serialisierung selbst zur
Decke → Bestand in Buckets sharden (Modellierungsthema, §14).

---

## 18. Human-in-the-Loop und die Workflow-Frage: Warten ist Zustand, kein Thread

→ §4 (Stop-the-line), [ADR-003](adr/adr-003-write-path.md) (expectedVersion),
[ADR-013](adr/adr-013-event-log.md) (Events)

Zwei scheinbar verschiedene Fragen — "kann ein Mensch im Feed mitentscheiden?"
und "kann ich darauf eine Workflow-Engine bauen?" — haben dieselbe Antwort,
weil sie dasselbe Muster sind.

**Die harte Regel zuerst: Ein Feed-Handler wartet nie auf einen Menschen.**
Stop-the-line (§4) bedeutet: Solange ein Handler nicht zurückkehrt, rückt sein
Checkpoint nicht vor — ein blockierender Handler hält *seinen gesamten Feed* an
und endet nach dem Backoff als Poison-Eintrag. Menschen antworten in Stunden
oder Tagen; kein Thread, kein Prozess, kein Deployment überlebt das zuverlässig.

Der richtige Mechanismus dreht das Warten um: **Der Handler materialisiert die
Frage als Dokument und ist fertig.**

```csharp
// Handler auf OrderPlaced: braucht der Auftrag eine Freigabe?
public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
{
    await using var session = _store.OpenSession(change.Scope);
    // Deterministische ID: dieselbe Zustellung erzeugt denselben Task (Idempotenz!)
    var taskId = $"approval-{change.DocumentId}-v{change.Version}";
    await session.SaveAsync(new ApprovalTask(taskId, change.DocumentId,
        Status: ApprovalStatus.Pending, RequestedAt: _clock.UtcNow), expectedVersion: 0);
    await session.CommitAsync();
}
```

Der Checkpoint rückt vor, der Feed läuft weiter. Das Warten lebt jetzt **im
Store als persistierter Zustand** — crash-sicher, deploybar, beliebig lang.
Die menschliche Entscheidung ist dann ein ganz normaler Write:

```csharp
await session.PatchAsync<ApprovalTask>(taskId, p => p
    .Set(x => x.Status, ApprovalStatus.Approved)
    .Set(x => x.DecidedBy, actorId),
    expectedVersion: 1); // zwei Approver gleichzeitig → einer verliert typisiert
```

Und dieser Write erzeugt selbst einen Change, auf den der nächste Handler
reagiert. Der "Loop" durch den Menschen ist also kein blockierter Aufruf,
sondern eine Kette: *Change → Task-Dokument → menschlicher Write → Change →
nächster Schritt.* Jedes Glied ist atomar, versioniert und auditierbar.

Zwei Fallen, die das Muster entschärft:

1. **At-least-once**: Crasht der Prozess zwischen Task-Anlage und
   Checkpoint-Fortschritt, wird der Change erneut zugestellt. Deterministische
   Task-IDs (aus auslösender Dokument-ID + Version) machen die Wiederholung zum
   harmlosen Konflikt statt zum Duplikat.
2. **Konkurrierende Entscheider**: `expectedVersion` auf dem Task serialisiert
   die Entscheidung — der zweite Approver bekommt die `ConcurrencyException`
   und sieht in der UI "bereits entschieden von X" (per `GetHistoryAsync`).

**Die Workflow-Engine ist dieses Muster, generalisiert.** Das klassische
Saga-/Process-Manager-Modell braucht vier Dinge, und alle vier sind Primitive
des Kernels:

| Workflow-Bedarf | Kernel-Primitiv |
|---|---|
| Durable Zustandsmaschine pro Instanz | Workflow-Dokument (`currentStep`, Daten, Correlation) |
| Trigger auf Fakten reagieren | Change- und Event-Feed-Handler |
| Transitionen gegen Races schützen | `expectedVersion` beim Schreiben der Instanz |
| Vollständiges Ausführungsprotokoll | Change Feed der Instanz — jede Transition mit Actor, Zeit, Correlation, reversibel |

Ein Handler lädt die Instanz, entscheidet die Transition (reine Funktion:
Zustand + Auslöser → neuer Zustand), schreibt mit `expectedVersion`. Feuern
zwei Trigger gleichzeitig, verliert einer sauber und wertet beim Retry den
*neuen* Zustand aus — genau die Semantik, die man bei Zustandsmaschinen will.
Kompensation ("Saga-Rollback") sind Business-Events plus Handler, die rückwärts
aufräumen. Human-Tasks sind der Abschnitt oben.

**Das einzige fehlende Primitiv sind Timer** ("eskaliere nach 48 h ohne
Antwort"). Bewusst: Ein Scheduler ist eine eigene Verantwortung mit eigenen
Garantien. Das Rezept ist klein — `dueAt` als Feld auf der Instanz (per
Key-Mapping oder View indiziert abfragbar) plus ein Hosted Service, der
periodisch fällige Instanzen pollt und ein `WorkflowTimerFired`-Event appended;
ab da übernimmt wieder die normale Handler-Kette. Leader-Koordination für
diesen Poller gibt es mit `FOR UPDATE SKIP LOCKED` (§11) schon als Vorbild.

Was der Kernel *nicht* wird: eine BPMN-Engine mit DSL und Designer. Der Kernel
liefert durable State, reaktive Feeds, atomare Transitionen und das
Audit-Protokoll — die Workflow-*Definition* (welche Schritte, welche Regeln)
ist Anwendungscode oder ein späteres separates Paket darüber. Diese Grenze ist
dieselbe wie bei DSGVO (ADR-015): Mechanismen unten, Entscheidungen oben.

---

## 19. Checkpoints, Backup und Rebuild: Was ist Wahrheit, was ist ableitbar?

→ [ADR-009](adr/adr-009-change-feed-consumption.md) (Checkpoints),
[ADR-013](adr/adr-013-event-log.md) (Retention), §16 (Projektionen)

**Woher weiß ein Prozessor nach dem Neustart, wo er war?** Aus
`papuma.checkpoint`: eine Zeile pro Handler (`handler_name → last_seq`), in
derselben Datenbank wie der Feed selbst. Nach jedem erfolgreich verarbeiteten
Record schreibt der Prozessor `last_seq` fort; beim Start liest er die Zeile
und setzt exakt dort fort — egal ob der Prozess sauber heruntergefahren wurde,
gecrasht ist oder auf eine andere Maschine umgezogen ist. Event-Handler nutzen
dieselbe Tabelle mit dem Präfix `event:`. Es gibt keinen In-Memory-Zustand,
der verloren gehen könnte: Die Position *ist* eine Datenbankzeile.

Daraus folgt die Garantie **at-least-once**: Der Checkpoint rückt erst vor,
nachdem der Handler erfolgreich war. Crasht der Prozess dazwischen, wird der
Record erneut zugestellt — deshalb die Idempotenz-Pflicht für Handler (und das
deterministische-ID-Muster aus §18). Die Alternative — Checkpoint *vor* dem
Handler — wäre at-most-once: kein Duplikat, aber stille Lücken in der
Projektion. Für abgeleiteten Zustand ist "doppelt, aber idempotent" die einzig
richtige Wahl.

**Wann baut man eine Projektion neu auf?** Vier typische Situationen:

1. **Bug in der Handler-Logik** — die Projektion ist falsch *berechnet*. Fix
   deployen, `ResetCheckpointAsync(handlerName)`, Replay von seq 0.
2. **Neue Projektion** — ein frisch registrierter Handler beginnt bei seq 0.
   Der "Rebuild" ist also kein Spezialmodus, sondern der Normalfall des ersten
   Starts: Jede Projektion entsteht als Replay der gesamten Historie.
3. **Read-Model-Schemaänderung** — die neue Spalte braucht historische Werte,
   die nur im Feed stehen.
4. **Projektionsziel verloren** — Elasticsearch-Index gelöscht, Cache geleert,
   externe Datenbank restauriert.

Die harte Grenze: Reset nur für **Projektionen** (idempotent, ableitbar) —
niemals für Effekt-Handler. Ein zurückgesetzter E-Mail-Handler verschickt die
gesamte Mail-Historie erneut. Die Unterscheidung "Projektion vs. Effekt" ist
eine Design-Entscheidung pro Handler, die man beim Schreiben trifft, nicht
beim Reset.

**Was gehört ins Backup?** Logisch nur die Wahrheit: `papuma.document` (der
Zustand), `papuma.change` (die vollständige Historie) und `papuma.event` (die
Fakten) — plus die trivialen Infrastrukturtabellen `checkpoint`/`failure`.
Projektionen sind per Definition ableitbar. Aber zwei Einschränkungen machen
die reine Lehre praxistauglich:

1. **Events mit Retention sind die Ausnahme von der Ableitbarkeit.** Changes
   werden nie gelöscht — die Versionshistorie ist vollständig, jede
   Dokument-Projektion bleibt für immer rekonstruierbar (modulo
   Redaction-Marker, §8). Gepurgte Events sind dagegen *weg* (ADR-013:
   Faktenspeicher, kein Versionsspeicher). Eine Projektion über Events mit
   Retention ist **nicht** aus dem Log rekonstruierbar: entweder ihren Zustand
   mitsichern oder die Retention deutlich länger wählen als jedes denkbare
   Rebuild-Bedürfnis.
2. **Rebuild kostet Zeit.** Projektionen mitzusichern ist keine Korrektheits-,
   sondern eine Recovery-Time-Entscheidung: Restore + Voll-Replay von Jahren
   an Changes kann Stunden dauern, in denen Read-Models fehlen. Liegen die
   Projektionen in derselben Postgres-Instanz, ist die Frage ohnehin müßig —
   `pg_dump`/PITR sichern sie als konsistenten Snapshot mit, *inklusive der
   exakt dazu passenden Checkpoints* (das ist der stille Vorteil davon, dass
   Checkpoints in derselben Datenbank leben: Snapshot-Konsistenz gratis).

Bei **externen Zielen** (Elasticsearch, Redis, Fremdsystem) gilt nach einem
Restore die einfache Regel: nicht hoffen, dass externer Zustand und
restaurierter Checkpoint zueinander passen — Checkpoint resetten und neu
aufbauen. At-least-once plus Idempotenz machen genau das gefahrlos.

---

## 20. Snapshots: Das Konzept existiert — invertiert

→ [ADR-002](adr/adr-002-derived-change-feed.md) (Dokument = Wahrheit),
§6 (reversible Diffs), §19 (Rebuild)

Eventsourcing-Systeme kennen **Snapshots**: periodisch persistierte
Zwischenstände, damit ein Aggregat-Load nicht den gesamten Event-Stream
replayen muss. Die Frage "gibt es das hier auch?" hat eine hübsche Antwort:
Ja — aber invertiert. **`papuma.document` *ist* der Snapshot.**

Im klassischen Event Sourcing sind die Events die Wahrheit und der Zustand ist
abgeleitet; der Snapshot ist ein Cache, den ein Hintergrundprozess pflegt und
der veralten kann. Document-Sourced CQRS dreht das Verhältnis um: Der Zustand
ist die Wahrheit, der Feed ist abgeleitet (ADR-002). Der "Snapshot" wird damit
in **derselben Transaktion** wie jeder Change geschrieben — er kann per
Konstruktion weder veralten noch hinterherhinken, und das Problem, das
Snapshots lösen, existiert an seiner Hauptstelle gar nicht. Im Einzelnen, an
den drei Orten, wo klassische Systeme Snapshots brauchen:

1. **Aggregat laden**: `LoadAsync` ist ein einzelner Zeilen-Read. Kein Replay,
   nie — unabhängig davon, ob das Dokument 3 oder 30 000 Versionen hat.
2. **Time-Travel** ("Dokument bei Version 12"): Weil Diffs reversibel sind
   (§6), rekonstruiert man historische Zustände **rückwärts vom aktuellen
   Dokument** statt vorwärts von Version 0. Der nächstgelegene Snapshot ist
   immer der Kopf: Version 498 von 500 kostet zwei Reverse-Applies statt 498
   Forward-Applies — und je näher die gesuchte Version an der Gegenwart liegt
   (der häufige Fall: Konflikt-UIs, "was hat sich gerade geändert?"), desto
   billiger. Caveat: redactete Felder blockieren die Rückwärtsreise (§8) —
   gewollt, sonst wären Policies wertlos.
3. **Projektions-Rebuild**: die einzige Stelle, an der "den ganzen Feed
   durchlaufen" real existiert (§19). Aber Projektionen sind persistent und
   gecheckpointet — sie rebuilden *nicht* beim Start, sondern nur bei
   explizitem Reset. Und der Reset passiert fast immer, weil sich die
   *Handler-Logik* geändert hat — genau dann wäre ein Projektion-Snapshot
   **ohnehin ungültig**, weil mit alter Logik berechnet. Das ist die
   klassische Snapshot-Falle im Event Sourcing (Snapshot-Invalidierung bei
   Logikänderung wird gern vergessen); hier stellt sie sich nicht, weil der
   Reset-auf-0-Fall der einzige verbleibende ist und der Snapshot dort nichts
   Gültiges beitragen könnte.

Bräuchte man je einen Zwischenstand für eine *sehr* teure Projektion mit
*stabiler* Logik, ist er trivial: Die Projektion persistiert ihren eigenen
Zustand — das tut sie als Read-Model sowieso — und ihr Checkpoint ist die
zugehörige Position. "Projektion + Checkpoint" *ist* das Snapshot-Paar; es
gibt nichts Zusätzliches zu erfinden.

**Backup-Nebenfrage**: In klassischen Systemen sind Snapshots abgeleitet und
damit optional im Backup (rebuildbar aus den Events — dieselbe
Recovery-Time-Abwägung wie §19). Hier ist `papuma.document` die Wahrheit
selbst und damit der Kern jedes Backups; die Frage löst sich auf.

---

*Pflegehinweis: Neue Erklärstücke aus späteren Phasen hier ergänzen — dieses
Dokument ist der Sammelpunkt für das "Warum hinter dem Wie" und Rohstoff für die
Tutorials (Phase 9).*
