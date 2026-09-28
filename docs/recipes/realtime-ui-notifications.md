# Recipe: Realtime UI notifications on document changes

Status: verified against the implemented API (phase 9, 2026-06-11) — type names
and signatures match `Papuma.Kernel.Processing` / `Papuma.Kernel.Changes`
([ADR-009](../adr/adr-009-projections-as-dumb-handlers.md),
[ADR-010](../adr/adr-010-feed-consumption.md)).

## Scenario

Multiple users work on the same data concurrently (e.g. a user master-data
editor). As soon as someone saves, all other clients displaying the same document
should be informed immediately — including *which fields* changed. The UI can
then react **before** the second user hits save and runs into the
`ConcurrencyException` (a proactive complement to the reactive conflict case from
[ADR-003](../adr/adr-003-write-path-concurrency.md)).

## Why this is almost free

| Property | Where it comes from |
|---|---|
| Near-realtime (ms instead of the poll interval) | the processing engine's LISTEN/NOTIFY wakeup (ADR-010) |
| "What changed?" inside the push | the diff lives in the `ChangeRecord` (ADR-004) |
| No PII in the push | policies act before the write — the handler only sees the policy-applied diff (ADR-007) |
| At-least-once, checkpoints, retry | processing-engine infrastructure (ADR-009) |

## The handler

An ordinary `IChangeHandler` — the kernel knows no SignalR; the handler is
application code:

```csharp
[StartsAtFeedHead] // an effect: pushing history to today's clients means nothing (ADR-024)
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
                ChangedFields = change.Diff.Paths    // policy-applied!
            }, ct);
    }
}
```

Notes:

- **Push only paths, never values.** `Diff.Paths` is enough for the UI to say
  "field X was changed" and avoids document contents flowing unfiltered through
  the hub. Whoever needs values reloads the document explicitly (the authorized
  read path).
- **Idempotency is trivially satisfied**: a doubly sent "documentChanged" is
  harmless (the engine's at-least-once semantics, ADR-009).
- **Detect version jumps**: using `Version`, the UI can tell whether it missed
  notifications (locally known version + 1 ≠ pushed version → reload the
  document).

## Hub and group management (sketch)

Clients join the group when opening a document and leave it when closing:

```csharp
public sealed class DocumentHub : Hub
{
    public Task Watch(string documentType, string documentId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"{documentType}/{documentId}");

    public Task Unwatch(string documentType, string documentId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"{documentType}/{documentId}");
}
```

Client side (sketch):

```javascript
connection.on("documentChanged", ({ documentId, version, changedFields }) => {
    showBanner(`This document was just changed (${changedFields.join(", ")}).`);
    // optionally: highlight fields, offer a reload, add a warning to the save button
});

await connection.invoke("watch", "User", userId);
```

> 🔐 **Do not forget authorization**: `Watch` must check whether the user may see
> the document at all (tenant scope + domain permissions) — otherwise even the
> change *paths* leak information.

## Registration

```csharp
builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("papuma");
        o.Model(m => m.Document<User>());
    })
    .AddChangeHandler<DocumentChangedNotifier>();

builder.Services.AddSignalR();
builder.Services.AddHealthChecks().AddPapumaChangeFeedLag(maxAllowedLag: 1000);
```

The bootstrap hosts the feed workers automatically (NOTIFY-driven, polling as the
fallback) and creates the schema at startup.

## Out of scope: "currently being edited" (presence)

Deliberately **not** part of this recipe and not part of the kernel: the
information "user X currently has the document open in an editor" is neither a
state change nor a fact worth remembering — it falls through all three filters of
the decision rule from [ADR-013](../adr/adr-013-business-event-log.md) (*state →
document, fact → event log, trigger → handler*). Presence is ephemeral
information with TTL semantics and belongs entirely in the application's
transport layer (SignalR groups directly, in-memory, Redis presence) — never in
`papuma.document` or `papuma.event`.
