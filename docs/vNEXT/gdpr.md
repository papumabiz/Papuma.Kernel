# Papuma vNEXT — DSGVO-Guide

Status: Verifiziert gegen die implementierte API (Phase 12, 2026-06-12)

Grundprinzip (ADR-015): **Der Kernel liefert ausführende Mechanismen über das, was
nur er kennt** (Metamodell, Historie, Scopes) — **die Anwendung trifft die
fachlich-juristischen Entscheidungen** (welche Daten gehören zur Person, Löschen
vs. Einschränken vs. Aufbewahren pro Tenant).

## Erste Verteidigungslinie: PII-Policy-Disziplin

Alle personenbezogenen Felder gehören unter Policy (ADR-007) — dann ist der Feed
von Haus aus minimiert und die scharfen Werkzeuge unten sind nur das Sicherheitsnetz:

```csharp
private sealed record User(
    string Id,
    string Name,                                      // bewusst Track? → Inventar prüfen!
    [property: UniqueKey] string Email,
    [property: SensitiveData] string? Iban,           // Diff: nur "changed"
    [property: TrackHash] string? PasswordHash);      // Diff: Marker + SHA-256
```

## Daten-Inventar (Art.-30-Unterstützung)

Reiner Metamodell-Report, kein Datenbankzugriff — als Verzeichnis-Anhang und als
**Review-Werkzeug** ("welche Felder sind ungeschützt?"), z. B. als Snapshot-Test in CI:

```csharp
DataInventoryReport report = DataInventory.Build(model);

foreach (var doc in report.Documents)
{
    Console.WriteLine($"{doc.Name}: ungeschützt = [{string.Join(", ", doc.UnprotectedPaths)}]");
}

File.WriteAllText("art30-inventory.json", report.ToJson().ToJsonString());
```

Der Report listet pro Dokumenttyp alle Blatt-Pfade mit effektiver Policy
(Attribut-Defaults, Fluent-Overrides und Vererbung aufgelöst), Keys und
Schema-Version; pro Event-Typ die Payload-Pfade und die Retention.

## Export (Art. 15 Auskunft / Art. 20 Portabilität)

Die Anwendung liefert das Subjekt→Daten-Mapping (über ihre Keys und Projektionen),
der Kernel assembliert in **einer Transaktion** (konsistenter Snapshot):

```csharp
JsonObject export = await GdprExport.ExportAsync(store, scope,
    documents: [new DocumentRef("User", userId), new DocumentRef("Order", orderId)],
    events: [new EventSelector("UserLoggedIn", "userId", userId)]);
```

Inhalt pro Dokument: aktueller Zustand (`exists: false` bei gelöschten — die
Historie kommt trotzdem), Versionen, vollständige Änderungshistorie mit Diffs und
Metadaten. Events werden generisch über Payload-Pfad = Wert selektiert; überlappende
Selektoren dedupliziert der Export. **Die Policy-Minimierung wirkt automatisch**:
Diffs und Event-Payloads liegen policy-bereinigt im Store — redactete Werte
erscheinen auch im Export nur als Änderungsmarker. Der aktuelle Dokumentzustand ist
dagegen die gespeicherte Wahrheit im Klartext (das ist der Sinn der Auskunft).

## Löschung (Art. 17) — das Tenant-Muster

Die Rechtslage unterscheidet sich pro Tenant: Tenant A darf wirklich löschen,
Tenant B hat Aufbewahrungspflichten (HGB/AO, 6–10 Jahre). Die Scope-Isolation
garantiert strukturell, dass die Exekution in A den Tenant B nicht berührt.

**Tenant A — echte Löschung:**

```csharp
await using var session = store.OpenSession(scopeA,
    new SessionOptions { ActorId = "dpo@firma.de" });

await session.DeleteAsync<User>(userId, expectedVersion);          // Zustand weg
await session.RedactHistoryAsync<User>(userId,                     // Historie bereinigt
    reason: "erasure-request-4711");
await session.RedactEventsAsync<UserLoggedIn>(                     // Events bereinigt
    selectorPath: "userId", selectorValue: userId,
    paths: ["ip", "userAgent"], reason: "erasure-request-4711");
await session.CommitAsync();
```

**Tenant B — Einschränkung statt Löschung (Art. 18):** Sperrstatus als Dokumentfeld,
Verarbeitung anwendungsseitig einschränken, Lösch-Fälligkeit vormerken — und nach
Fristablauf terminiert dasselbe Lösch-Primitiv aufrufen (Workflow-Muster: concepts §18).

```csharp
await session.PatchAsync<User>(userId, p => p
    .Set(x => x.ProcessingRestricted, true)
    .Set(x => x.EraseAfter, new DateOnly(2036, 6, 12)));
```

## `RedactHistoryAsync` / `RedactEventsAsync` — das Sicherheitsnetz

Dokument-Löschung bereinigt den Feed nur für policy-geschützte Felder — ein
getracktes PII-Feld (z. B. `Name` ohne Attribut) bleibt nach dem Delete im Klartext
in historischen Diffs. Genau diese Lücke schließen die Redaction-Primitive:

- `RedactHistoryAsync<T>(id, reason, paths?)` schreibt historische Diff-Einträge
  auf den Redacted-Marker um (`paths` deckt Nachfahren ab; `null` = alles).
  Funktioniert auch für bereits gelöschte Dokumente. Idempotent.
- `RedactEventsAsync<TEvent>(selectorPath, selectorValue, paths, reason)` entfernt
  Payload-Felder aus selektierten Events (Semantik der Redact-Event-Policy:
  Feld fehlt, Konsumenten sehen Defaults).

Beide sind **irreversibel** und verlangen einen Audit-Grund; jede umgeschriebene
Zeile erhält einen `redaction`-Block in den Metadaten (wann, warum, Actor,
Correlation). Konsequenz nach ADR-008: Rollback über redactete Historie scheitert
typisiert (`RollbackNotPossibleException`) — die Werte sind weg, absichtlich.

Das verletzt bewusst die Append-only-Reinheit des Feeds: **Art. 17 schlägt
Architekturästhetik.** Redaction ist kein Bestandteil normaler Anwendungsabläufe —
wer es regelmäßig braucht, hat ein Policy-Versäumnis (→ Inventar-Review).

## Was bewusst Anwendungssache bleibt

| Aufgabe | Warum nicht im Kernel |
|---|---|
| Subjekt → Dokumente/Events mappen | Domänenwissen (Keys, Projektionen) |
| Löschen vs. Einschränken vs. Aufbewahren pro Tenant | juristische Konfiguration |
| Fristen-Scheduling ("nach 10 Jahren löschen") | Workflow (concepts §18: `dueAt` + Poller) |
| Export-Auslieferung (Format, Verschlüsselung, Zustellweg) | Produktentscheidung |
