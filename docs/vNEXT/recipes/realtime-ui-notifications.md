# Rezept: Echtzeit-UI-Benachrichtigungen über Dokumentänderungen

Status: Entwurf für späteres Tutorial (2026-06-11)

> ⚠️ vNEXT ist noch nicht implementiert — Typnamen und Signaturen folgen den ADRs
> ([ADR-009](../adr/adr-009-projections-as-dumb-handlers.md),
> [ADR-010](../adr/adr-010-feed-consumption.md)) und können sich während der
> Implementierung noch ändern. Konzept und Schnitt sind verbindlich.

## Szenario

Mehrere Benutzer arbeiten gleichzeitig auf denselben Daten (z. B. ein User-Stammdaten-
Editor). Sobald jemand speichert, sollen alle anderen Clients, die dasselbe Dokument
anzeigen, sofort informiert werden — inklusive der Information, *welche Felder* sich
geändert haben. So kann die UI reagieren, **bevor** der zweite Benutzer auf Speichern
drückt und in die `ConcurrencyException` läuft (proaktive Ergänzung zum reaktiven
Konfliktfall aus [ADR-003](../adr/adr-003-write-path-concurrency.md)).

## Warum das fast gratis ist

| Eigenschaft | Woher sie kommt |
|---|---|
| Nahezu Echtzeit (ms statt Poll-Intervall) | LISTEN/NOTIFY-Wakeup der Processing-Engine (ADR-010) |
| "Was hat sich geändert?" im Push | Der Diff liegt im `ChangeRecord` (ADR-004) |
| Keine PII im Push | Policies wirken vor dem Schreiben — der Handler sieht nur den policy-bereinigten Diff (ADR-007) |
| At-least-once, Checkpoints, Retry | Processing-Engine-Infrastruktur (ADR-009) |

## Der Handler

Ein gewöhnlicher `IChangeHandler` — der Kernel kennt kein SignalR, der Handler ist
Applikationscode:

```csharp
public sealed class DocumentChangedNotifier : IChangeHandler
{
    private readonly IHubContext<DocumentHub> _hub;

    public DocumentChangedNotifier(IHubContext<DocumentHub> hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    public string Name => "ui-notifier";

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        await _hub.Clients
            .Group($"{change.DocumentType}/{change.DocumentId}")
            .SendAsync("documentChanged", new
            {
                change.DocumentId,
                change.Version,
                change.Operation,                    // Insert | Update | Delete
                ChangedFields = change.Diff.Paths    // policy-bereinigt!
            }, ct);
    }
}
```

Hinweise:

- **Nur Pfade pushen, keine Werte.** `Diff.Paths` reicht der UI für "Feld X wurde
  geändert" und vermeidet, dass Dokumentinhalte ungefiltert über den Hub laufen. Wer
  Werte braucht, lädt das Dokument gezielt nach (autorisierter Read-Pfad).
- **Idempotenz ist trivial erfüllt**: Ein doppelt gesendetes "documentChanged" ist
  harmlos (At-least-once-Semantik der Engine, ADR-009).
- **Versionssprünge erkennen**: Die UI kann anhand `Version` erkennen, ob sie
  Benachrichtigungen verpasst hat (lokal bekannte Version + 1 ≠ gepushte Version →
  Dokument neu laden).

## Hub und Gruppen-Verwaltung (Skizze)

Clients treten beim Öffnen eines Dokuments der Gruppe bei, beim Schließen aus:

```csharp
public sealed class DocumentHub : Hub
{
    public Task Watch(string documentType, string documentId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"{documentType}/{documentId}");

    public Task Unwatch(string documentType, string documentId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"{documentType}/{documentId}");
}
```

Client-Seite (Skizze):

```javascript
connection.on("documentChanged", ({ documentId, version, changedFields }) => {
    showBanner(`Dieses Dokument wurde gerade geändert (${changedFields.join(", ")}).`);
    // optional: Felder markieren, Reload anbieten, Save-Button mit Warnung versehen
});

await connection.invoke("watch", "User", userId);
```

> 🔐 **Autorisierung nicht vergessen**: `Watch` muss prüfen, ob der Benutzer das
> Dokument überhaupt sehen darf (Tenant-Scope + Fachrechte) — sonst leaken schon die
> Änderungs*pfade* Informationen.

## Registrierung

```csharp
services.AddPapumaKernel(o => { /* ... */ })
        .AddChangeHandler<DocumentChangedNotifier>();
```

## Abgrenzung: "wird gerade bearbeitet" (Presence)

Bewusst **nicht** Teil dieses Rezepts und nicht Teil des Kernels: Die Information
"Benutzer X hat das Dokument gerade im Editor offen" ist keine Zustandsänderung und
kein erinnernswertes Faktum — sie fällt durch alle drei Raster der Entscheidungsregel
aus [ADR-013](../adr/adr-013-business-event-log.md) (*Zustand → Dokument, Faktum →
Event-Log, Auslösen → Handler*). Presence ist flüchtige Information mit TTL-Semantik
und gehört vollständig in die Transportschicht der Anwendung (SignalR-Groups direkt,
In-Memory, Redis-Presence) — niemals in `papuma.document` oder `papuma.event`.
