# ADR-012: Partielle Updates als Patch-Primitiv

## Status

Accepted (2026-06-11)

## Kontext

Der Standard-Write-Pfad (ADR-003) ist Load → Modify → Save mit komplettem
Dokument-Replace. Häufige Fälle ändern aber nur einzelne Felder (DisplayName,
Passwort, Zähler). Ein voller Zyklus dafür bedeutet: ein Lese-Roundtrip, Serialisierung
des Gesamtdokuments und künstliche Concurrency-Konflikte zwischen Schreibern, die
gar nicht dieselben Felder berühren.

## Entscheidung

1. Neben `Save` gibt es ein zweites Write-Primitiv **`Patch`**:

   ```csharp
   await session.PatchAsync<User>(id, p => p
       .Set(x => x.DisplayName, "Harry")
       .Remove(x => x.Nickname)
       .Increment(x => x.LoginCount));
   ```

2. **Kein vorheriges Laden — weder durch den Aufrufer noch intern.** Ein Patch ist ein
   einziges atomares Statement; der "Read" findet innerhalb des `UPDATE` statt:

   ```sql
   UPDATE papuma.document
   SET data = jsonb_set(data, '{displayName}', @value),
       version = version + 1,
       updated_at = now()
   WHERE tenant_id = @tenantId AND document_type = 'User' AND id = @id
   RETURNING old.data, new.data, new.version;
   ```

   `RETURNING OLD/NEW` (ADR-001/003) liefert beide Zustände, ohne dass das Dokument je
   den Client erreicht hat. Daraus entstehen Diff, Policies und ChangeRecord exakt wie
   beim vollen Save — in derselben Transaktion. 0 Zeilen → Dokument existiert nicht
   (`DocumentNotFoundException`).

3. **Operationskatalog bewusst minimal**: `Set`, `Remove`, `Increment` auf typisierte
   Property-Pfade. Kein Query-DSL, keine bedingten Ausdrücke, keine Array-Manipulation
   über Index-Setzen hinaus — wer mehr braucht, nutzt Load + Save.

4. **Concurrency ist beim Patch opt-in.**
   - Ohne `expectedVersion`: bewusstes **Field-Level Last-Writer-Wins** — korrekt für
     unabhängige Felder (zwei gleichzeitige Patches auf `displayName` und `phone`
     konfligieren nicht; die Versionsnummer serialisiert sie trotzdem sauber).
   - Mit `expectedVersion`: für Read-Modify-Write-Semantik, wenn der neue Wert von
     zuvor gelesenem Zustand abhängt.
   - Abhängigkeiten vom *aktuellen* Wert ohne Versionscheck drückt der Katalog als
     SQL-Ausdruck aus (`Increment`) — atomar ohne Konfliktfenster.

5. **Validierung bleibt möglich, ohne den Vorteil zu opfern**: Pro Dokumenttyp kann ein
   Validator registriert werden; der Kernel deserialisiert dann `new.data` aus dem
   RETURNING und validiert **vor dem Commit** — schlägt es fehl, Rollback und typisierte
   Exception. Default ist ohne Validator (Patch auf deklarativ "sichere" Felder).

6. **Version zählt immer weiter** (`version + 1`), der ChangeRecord ist von einem
   Save-ChangeRecord nicht unterscheidbar — Konsumenten (ADR-009) müssen Patches nicht
   kennen.

## Konsequenzen

- Feldänderungen kosten einen Roundtrip statt zwei und konfligieren nicht mit
  Schreibern anderer Felder.
- Diff, Policies (z. B. `[TrackHash]` beim Passwort) und Feed-Invarianten gelten
  unverändert — es gibt keinen "Policy-Bypass" über Patch.
- Last-Writer-Wins auf Feldebene ist eine bewusste Wahl des Aufrufers, nicht
  Default-Verhalten des Kernels (der Default `Save` verlangt weiterhin `expectedVersion`).
- Upcasting (ADR-005): Patch auf ein Dokument mit alter `schema_version` schlägt
  typisiert fehl, wenn der gepatchte Pfad von einem Upcaster betroffen wäre — der
  Aufrufer muss dann Load + Save nutzen (das hebt das Schema an). Patches auf
  schema-aktuelle Dokumente sind uneingeschränkt.
- Die Grenze des Katalogs ist Disziplin gegen ORM-Drift: Sobald jemand "bedingte
  Patches" fordert, ist die Antwort Load + Save, nicht Katalog-Erweiterung.
