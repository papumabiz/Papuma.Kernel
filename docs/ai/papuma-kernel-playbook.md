# Papuma.Kernel — Playbook für KI-Agenten

Status: Verifiziert gegen die implementierte API (Phase 13, 2026-06-12) ·
Zielgruppe: Coding-Agenten, die **Anwendungen entwickeln, die Papuma.Kernel nutzen**.

Dieses Dokument ist die Einstiegskarte. Die Wahrheit liegt in der Referenzkette:
[getting-started.md](../vNEXT/getting-started.md) (API in 5 Minuten) →
[concepts.md](../vNEXT/concepts.md) (das Warum, §1–§20) →
[ADRs](../vNEXT/adr/) (verbindliche Entscheidungen) →
[architecture.md](../vNEXT/architecture.md) (Datenmodell, Namespaces).

## Das Mentalmodell in vier Sätzen

1. **Das JSON-Dokument ist die Wahrheit.** Kein Event Sourcing: Zustand wird
   direkt gespeichert (`papuma.document`), der Change Feed wird als reversibler
   Feld-Diff *abgeleitet* — atomar im selben Statement (PostgreSQL ≥ 18,
   `RETURNING OLD/NEW`).
2. **Die Session ist eine Unit of Work.** Alle Writes einer Session committen
   atomar (`CommitAsync`) oder gar nicht (Dispose ohne Commit = Rollback).
3. **Reagiert wird über Feeds.** Change- und Event-Handler konsumieren strikt
   geordnet, checkpointed, at-least-once. Der Kernel generiert keine Projektionen —
   Handler sind "dumm" und schreiben, wohin sie wollen.
4. **Alles ist scope-gebunden.** Jede Session gehört einem Scope
   (`Platform` oder `Tenant(id)`); Isolation kommt zweischichtig (explizite
   Prädikate + Row Level Security).

## Harte Regeln (ADR-Verbote — nicht verhandelbar)

| Regel | Warum / Quelle |
|---|---|
| `SaveAsync` **immer** mit `expectedVersion` (`0` = Insert erwartet). Bei `ConcurrencyException`: neu laden, neu entscheiden — keine blinden Retry-Schleifen. | ADR-003; Konflikt-UI: `GetHistoryAsync(id, fromVersion: expected + 1)` |
| **Personenbezogene Felder vor dem ersten Save unter Policy stellen** (`[SensitiveData]`, `[TrackHash]`, `[DoNotTrack]` oder Fluent). Nachträglich hilft nur `RedactHistoryAsync` (scharfes Werkzeug). | ADR-007/015; Review: `DataInventory.Build(model).Documents[..].UnprotectedPaths` |
| **Kein Query-DSL erfinden oder wünschen.** Lesen geht über `LoadAsync`/`LoadByKeyAsync` (deklarierte Keys), SQL-Views (`security_invoker = on`!) oder eigene Projektionen. | ADR-009; concepts §16 |
| **Transformierende Schemaänderung nie additiv simulieren** (kein "neues Feld + altes behalten" für ein Rename). Renames/Restrukturierungen = `Upcast(fromVersion, …)`. Additive Änderungen (neues optionales Feld, Feld weg) brauchen *keinen* Upcaster. | ADR-005, Punkt 7 |
| **Handler sind idempotent** (at-least-once!) und **blockieren nie** — kein Warten auf Menschen/externe Antworten im Handler. Human-in-the-Loop = Task-Dokument schreiben, fertig. | ADR-009; concepts §18/§19 |
| **Checkpoint-Reset nur für Projektionen, nie für Effekt-Handler** (E-Mails würden erneut verschickt). Die Unterscheidung trifft man beim Schreiben des Handlers. | concepts §19 |
| **Events nur für Fakten ohne Zustandswahrheit** (`UserLoggedIn`). Zustandsübergänge gehören ins Dokument; Trigger in Handler. Events haben kein Upcasting — neue Form = neuer Event-Typ. | ADR-011/013 |
| **Begrenzte Zähler** (Lagerbestand, Kontingente): `Increment` + Typ-Validator — nicht Load-Check-Save-Schleifen. | ADR-012; concepts §17 |
| **Bedingte Patches gibt es nicht** und werden nicht ergänzt. Wer Bedingungen braucht: Load + Save mit `expectedVersion`. | ADR-012 |
| **Nie direkt in `papuma.*`-Tabellen schreiben.** Lesen per View ist legitim (mit den 4 Caveats aus concepts §16). | ADR-002 |
| Massen-Updates über `PatchWhereAsync`/`PatchManyAsync`/`DeleteWhereAsync` — nicht N Sessions in Schleife. | ADR-014 |

## API-Schnellkarte

```csharp
// Bootstrap (Anwendung)
services.AddPapumaKernel(o => { o.ConnectionString = …; o.Model(m => m
    .Document<User>(d => d.UniqueKey(x => x.Email).Validate(u => …))
    .Event<UserLoggedIn>(e => e.Retention(TimeSpan.FromDays(90)))); })
  .AddChangeHandler<UserProjection>()
  .AddEventHandler<LoginAudit>();

// Schreiben (Session = UoW; Scope ist Pflicht)
await using var s = store.OpenSession(ScopeContext.Tenant("acme"),
    new SessionOptions { ActorId = "harry", CausationId = "cmd-42" });
await s.SaveAsync(doc, expectedVersion);            // 0 = Insert
await s.PatchAsync<User>(id, p => p.Set(x => x.Name, "H").Increment(x => x.LoginCount));
await s.PatchWhereAsync<User>(x => x.Status, "old", p => p.Set(x => x.Status, "new"));
await s.DeleteAsync<User>(id, expectedVersion);
await s.RollbackAsync<User>(id, toVersion, expectedVersion);   // append-only
await s.AppendAsync(new UserLoggedIn(id, "web"));   // Event, atomar mit den Writes
await s.CommitAsync();                              // sonst: Rollback bei Dispose

// Lesen (strong consistency)
var r = await s.LoadAsync<User>(id);                // r.Document, r.Version
var byKey = await s.LoadByKeyAsync<User>(x => x.Email, "x@y.de");
var history = await s.GetHistoryAsync<User>(id, fromVersion: 3);

// Reagieren
public sealed class UserProjection : IChangeHandler
{
    public string Name => "user-projection";        // = Checkpoint-Identität!
    public Task HandleAsync(ChangeRecord change, CancellationToken ct) { … }
}

// Diagnose (Phase 11) · DSGVO (Phase 12)
await processor.GetLagAsync(); await processor.GetFailuresAsync();
await processor.RetryFailureAsync(name, seq); await processor.ResetCheckpointAsync(name);
DataInventory.Build(model);                         // Art. 30 + Policy-Review
await GdprExport.ExportAsync(store, scope, docRefs, eventSelectors);
await s.RedactHistoryAsync<User>(id, reason, paths);
```

Typisierte Fehler, die du behandeln (nicht wegfangen) sollst: `ConcurrencyException`,
`UniqueKeyViolationException`, `DocumentNotFoundException`,
`SchemaVersionConflictException` / `SchemaUpcastRequiredException`,
`RollbackNotPossibleException`.

## Entscheidungsbaum: "Ich will …"

| Bedarf | Werkzeug |
|---|---|
| …sofort konsistent lesen (Login, Geschäftslogik) | `LoadAsync` / `LoadByKeyAsync` |
| …per SQL/BI auf aktuelle Daten | View mit `security_invoker = on` (concepts §16) |
| …ein Read-Model / einen Suchindex pflegen | `IChangeHandler`-Projektion (eventual, Lag beobachtbar) |
| …ein einzelnes Feld ändern, ohne zu laden | `PatchAsync` (Field-Level-LWW ist dort bewusst) |
| …einen Bestand begrenzen (nie überverkaufen) | `Increment(-1)` + `Validate` (concepts §17) |
| …auf "Feld X wurde Y→Z" reagieren | `change.IsFieldTransition(path, from, to)` im Handler (ADR-011) |
| …eine menschliche Freigabe einbauen | Task-Dokument schreiben, Handler endet; Entscheidung = normaler Write (concepts §18) |
| …einen Workflow/Saga bauen | Workflow-Dokument + Feed-Handler + `expectedVersion`; Timer = `dueAt`-Poller (concepts §18) |
| …UI live aktualisieren | NOTIFY-Rezept ([realtime-ui-notifications](../vNEXT/recipes/realtime-ui-notifications.md)) |
| …Embeddings/RAG, NL-Audit, Anomalie-Erkennung | KI-Rezepte ([ai-consumers](../vNEXT/recipes/ai-consumers.md)) — Konsumenten des Feeds, keine Kernel-Features |
| …eine Auskunft/Löschung (DSGVO) bedienen | [gdpr.md](../vNEXT/gdpr.md) — Export, Inventar, Redaction |
| …Lag/Fehler einer laufenden App inspizieren | Diagnose-APIs ([observability.md](../vNEXT/observability.md)) oder MCP-Server (`Papuma.Kernel.Mcp`) |

## Wenn etwas klemmt

- **Lag wächst**: erst `papuma.feed.handler.duration` ansehen — fast immer ein
  langsamer Handler, nicht die Engine (concepts §14: Grenzen + Auswege).
- **Poison-Counter > 0**: `GetFailuresAsync()` → Ursache beheben →
  `RetryFailureAsync(handler, seq)`; liegt der Checkpoint dahinter, zusätzlich
  `ResetCheckpointAsync`.
- **`ConcurrencyException` häuft sich auf einem Dokument**: Hot Document.
  Prüfen, ob `PatchAsync` (unabhängige Felder) oder `Increment` (Zähler) das
  Load-Modify-Save ersetzen kann (concepts §4/§17).
- **RLS-Fehler/leere Reads in Workern**: `'All'`-Scope-Mechanismus prüfen
  (`SetAllScopesAsync` bzw. `ScopeFilter.All()`), Querschnitts-Checkliste im
  [Implementierungsplan](../vNEXT/implementation-plan.md).
- **Rollback scheitert typisiert**: Diffs auf dem Rückweg enthalten Policy-Einträge
  ohne Werte — das ist gewollt (concepts §8); Anwendungsfall neu denken statt
  Policy entfernen.

## Was der Kernel bewusst NICHT ist

Kein ORM, kein Query-DSL, keine Workflow-/BPMN-Engine, keine Projektion-Generierung,
kein Event Sourcing, keine LLM-Aufrufe im Kernel (deterministische Infrastruktur).
Wenn deine Lösung eines davon "im Kernel ergänzen" will: falsch abgebogen — die
Antwort steht fast sicher in einem ADR oder concepts-Paragraphen.
