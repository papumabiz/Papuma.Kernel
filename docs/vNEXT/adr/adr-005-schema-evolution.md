# ADR-005: Schema-Evolution über schema_version und Upcaster-Pipeline

## Status

Accepted (2026-06-11)

## Kontext

Wenn die C#-Klasse die Wahrheit definiert, aber in der Datenbank JSONB-Dokumente aus
früheren Klassenständen liegen, entsteht ohne Konzept genau das Migrationsproblem, vor
dem vNEXT flieht: umbenannte Properties, geänderte Typen, umstrukturierte Verschachtelung
brechen die Deserialisierung still oder laut. Das ist der bekannteste Schmerzpunkt von
Dokument-Stores (vgl. Marten-Upcasting). Die ursprüngliche vNEXT-Diskussion hatte hierfür
keine Antwort — deshalb dieses Tag-1-ADR.

## Entscheidung

1. **Jedes Dokument und jeder ChangeRecord trägt `schema_version`** (int, pro Dokumenttyp).
2. Pro Typ werden **Upcaster** registriert, die rohes JSON von Version n nach n+1 heben:

   ```csharp
   builder.For<User>()
       .Upcast(fromVersion: 1, json =>
       {
           json["email"] = json["mail"];
           json.AsObject().Remove("mail");
       })
       .Upcast(fromVersion: 2, json => /* ... */);
   ```

   Die aktuelle Schema-Version eines Typs ist `höchster fromVersion + 1`; ohne Upcaster ist sie 1.
3. **Upcasting passiert beim Laden (lazy)**, auf dem rohen `JsonNode`, vor der
   Deserialisierung. Es gibt keine Big-Bang-Datenmigration.
4. **Persistiert wird der neue Stand erst beim nächsten Save** (das Update schreibt
   `schema_version` mit). Optional kann ein Wartungs-Worker Dokumente proaktiv
   durch Laden+Speichern anheben — das ist Betriebsentscheidung, nicht Kernel-Zwang.
5. **ChangeRecords werden nie rückwirkend migriert.** Sie behalten die `schema_version`
   ihres Entstehungszeitpunkts. Beim Feed-Replay (Rebuild) kann die Engine die
   Upcaster-Pipeline optional auf Diff-Werte anwenden; Konsumenten, die das nicht
   nutzen, müssen mit historischen Schemata umgehen.
6. **Verbote, die der Kernel erzwingt**: Ein Save mit niedrigerer `schema_version` als
   der gespeicherten ist ein Fehler (verhindert, dass alte Deployments neue Dokumente
   stillschweigend zurückmigrieren).
7. **Additive Änderungen brauchen keinen Upcaster und keinen Versions-Bump.**
   Änderungen, die die Semantik bestehender Daten nicht berühren, deckt die
   JSON-Deserialisierung von selbst ab:

   - neues optionales Property mit **konstantem** Default,
   - Property entfernen (überzählige JSON-Felder werden ignoriert; beim nächsten
     Save verschwinden sie),
   - neuen Enum-Wert ergänzen.

   Upcaster + Versions-Bump sind nur für **transformierende** Änderungen nötig:
   Rename, Umstrukturierung, Typwechsel, abgeleitete (nicht-konstante) Defaults.
   Ausdrücklich verboten ist das **additive Simulieren von Transformationen**
   (neues Feld neben dem alten, Leser prüfen "wenn `email` leer, nimm `mail`") —
   damit verteilt sich Kompatibilitätslogik dauerhaft über alle Konsumenten.
   Die Regel hält Upcaster-Ketten kurz; sie ersetzt Upcaster nicht.

## Verworfene Alternative: Protobuf

Protobuf wurde als Evolutionsmechanismus geprüft und verworfen:

- **Opaker Blob statt JSONB**: Protobuf-Speicherung (`bytea`) verliert alles, worauf
  vNEXT steht — Expression-Indizes (ADR-006), SQL-inspizierbare Dokumente, queryable
  Diffs, Policies auf JSON-Pfaden (ADR-007).
- **Toleranz statt Transformation**: Protobufs Modell (Field Numbers, unbekannte Felder
  erhalten) verhindert Lesefehler, kann aber keine Semantik herstellen — ein Rename ohne
  beibehaltene Field Number ist stiller Datenverlust, Umstrukturierungen sind strukturell
  unmöglich. Upcaster leisten beliebige, testbare Transformationen.
- **IDL-Hybrid lohnt nicht**: `.proto` als Schemaquelle mit JSON-Mapping würde die
  Wahrheit von der C#-Klasse in protoc-generierte Typen verlagern und das Attribut-
  basierte Policy-Modell brechen.

Übernommen wird stattdessen Protobufs **Disziplin** (Punkt 7: additiv als Default).
Am Integrationsrand — etwa beim Publizieren übersetzter fachlicher Events an externe
Konsumenten (ADR-011) — bleibt Protobuf eine gute Wahl; das ist Handler-Sache, nicht
Kernel-Sache.

## Konsequenzen

- Klassenänderungen sind ein normaler, getesteter Vorgang: Upcaster schreiben + Version
  erhöhen, fertig. Upcaster sind pure Funktionen über JSON und damit trivial testbar.
- Lazy Upcasting bedeutet: alte Dokumente bleiben beliebig lange auf altem Stand liegen.
  Upcaster dürfen deshalb nie entfernt werden, solange Dokumente ihrer Quellversion
  existieren können.
- Punkt 6 erfordert disziplinierte Deployments (kein Rollback der Anwendung unter
  bereits hochmigrierte Dokumente) — der Fehler ist dann aber laut statt still.
