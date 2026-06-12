# AGENTS.md-Snippet für Anwendungen, die Papuma.Kernel nutzen

Kopiere den Block unten in die `AGENTS.md` / `CLAUDE.md` deiner Anwendung und passe
die Platzhalter an. Er gibt Agenten das Mentalmodell und die nicht verhandelbaren
Regeln — Details stehen im [Playbook](papuma-kernel-playbook.md) und in der Doku
des Pakets.

---

```markdown
## Persistenz: Papuma.Kernel (Document-Sourced CQRS, PostgreSQL ≥ 18)

Diese Anwendung speichert Zustand als JSON-Dokumente über Papuma.Kernel.
Das Dokument ist die Wahrheit; ein reversibler Change Feed und ein Event-Log
werden automatisch abgeleitet. KEIN Event Sourcing, KEIN ORM, KEIN Query-DSL.

### Regeln (verbindlich, aus den ADRs des Pakets)

1. Schreiben nur über `DocumentSession` (Unit of Work): `store.OpenSession(scope)`
   → Writes → `CommitAsync()`. Ohne Commit wird verworfen.
2. `SaveAsync` IMMER mit `expectedVersion` (`0` = Insert). `ConcurrencyException`
   behandeln: neu laden, neu entscheiden — keine blinden Retries.
3. Einzelfelder: `PatchAsync` (kein Load nötig). Begrenzte Zähler:
   `Increment` + registrierter Validator — niemals Load-Check-Save-Schleifen.
4. Personenbezogene Felder STEHEN UNTER POLICY, bevor sie je gespeichert werden:
   `[SensitiveData]` / `[TrackHash]` / `[DoNotTrack]` oder Fluent-Override.
5. Lesen: `LoadAsync`/`LoadByKeyAsync` (sofort konsistent), deklarierte Keys für
   Lookups, SQL-Views nur mit `security_invoker = on`. Kein Query-DSL erfinden.
6. Reagieren: `IChangeHandler`/`IEventHandler`. Handler sind IDEMPOTENT
   (at-least-once) und blockieren nie (kein Warten auf Menschen — stattdessen
   Task-Dokument schreiben). `Name` ist die Checkpoint-Identität: nie umbenennen.
7. Schema-Evolution: additive Änderungen einfach machen; Renames/Umbauten als
   `Upcast(fromVersion, …)` registrieren. Nie ein Rename additiv simulieren.
8. Events (`AppendAsync`) nur für Fakten ohne Zustandswahrheit (Login, Versand).
   Zustandsübergänge gehören ins Dokument.
9. Multi-Tenancy: jede Session ist scope-gebunden (`ScopeContext.Tenant(id)` /
   `Platform`). Nie Daten über Scopes hinweg mischen; Worker nutzen den
   'All'-Scope-Mechanismus.
10. Nie direkt in `papuma.*`-Tabellen schreiben oder deren Schema ändern.

### Platzhalter (anpassen)

- Scope-Auflösung: <WIE DIESE APP DEN TENANT BESTIMMT, z. B. Subdomain/Claim>
- Registrierte Dokumenttypen: <LISTE ODER VERWEIS AUF MODEL-BOOTSTRAP-DATEI>
- Projektionen vs. Effekt-Handler: <WELCHE HANDLER DÜRFEN RESETTET WERDEN>

### Referenz

- Playbook: docs/ai/papuma-kernel-playbook.md im Papuma.Kernel-Repo
- Konzepte (das Warum): docs/vNEXT/concepts.md §1–§20
- DSGVO-Werkzeuge: docs/vNEXT/gdpr.md · Diagnose: docs/vNEXT/observability.md
```
