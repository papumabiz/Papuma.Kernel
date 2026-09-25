# Tutorial — build your first Papuma app

Status: verified end-to-end against the implemented API (2026-06-13, PostgreSQL
18 via container).

> **PostgreSQL kernel only.** Building on `Papuma.Kernel.Local` (SQLite)? The
> domain steps carry over, the setup does not — read the playbook's
> [Differences section](ai/papuma-kernel-playbook.md#differences-when-using-papumakernellocal-sqlite-embedded) first.

This is the **learning-oriented** path: you will build one small application from
an empty folder to a running app where a change you make to a document flows —
untouched by any wiring of yours — into a live read model. By the end you will
have *felt* how the pieces fit, not just read about them.

It is deliberately **style-neutral**: you learn the kernel primitives as they
are (document → session → feed → handler). When you finish, the closing signpost
points to the two architectures the samples demonstrate — pick one then, or
none.

- For the terse five-minute API tour, see [getting-started.md](getting-started.md).
- For the *why* behind every step, see [concepts.md](concepts.md) (linked inline).
- This tutorial teaches by building; it does not re-explain — every concept gets
  one line and a link.

What we build: a tiny **support desk**. A ticket is a document; changing its
status drives a live "open tickets" counter; the change history doubles as an
audit trail with the requester's email automatically kept out of the feed.

---

## 0. Before you start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/) and a **PostgreSQL ≥ 18**
([ADR-001](adr/adr-001-postgresql-18-only.md) — the single-statement write path
depends on it). The fastest Postgres is a container:

```bash
docker run -d --name papuma-tut -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=papuma_tut -p 5432:5432 postgres:18-alpine
```

> If your Postgres runs in a container published on IPv4 only, use `127.0.0.1`
> in the connection string rather than `localhost` (which may resolve to `::1`).

---

## 1. An empty project

```bash
dotnet new web -n SupportDesk
cd SupportDesk
dotnet add package Papuma.Kernel
dotnet add package Papuma.Kernel.AspNetCore
```

`Papuma.Kernel` is the core (store, feed, policies); `Papuma.Kernel.AspNetCore`
adds the per-request tenant plumbing we use in step 4. Delete the scaffolded
`/` endpoint in `Program.cs` — we will fill it in as we go.

---

## 2. Your first document

A document is an ordinary C# record. The only convention is a `string Id`. One
attribute already does real work: `[SensitiveData]` tells the kernel to keep the
requester's email out of the change feed ([ADR-007](adr/adr-007-privacy-policies.md)
— privacy is declared at the place of truth, not bolted on later). Add this to
`Program.cs` (bottom of the file is fine for a tutorial):

```csharp
public sealed record SupportTicket(
    string Id,
    string Subject,
    [property: SensitiveData] string RequesterEmail,
    TicketStatus Status = TicketStatus.Open);

public enum TicketStatus { Open, Resolved }
```

That is the whole schema. There is no migration, no table definition — the
kernel derives storage from the model in the next step.

---

## 3. Bootstrap

Replace the body of `Program.cs` above the records with the bootstrap. This
registers the store, **creates the schema idempotently at startup** (tables,
row-level security, key indexes) and starts the feed workers:

```csharp
// These usings cover the whole file as we grow it through the tutorial:
using Papuma.Kernel.AspNetCore.Tenancy;  // AddPapumaScope, UseScopeResolution, GetScopeContext
using Papuma.Kernel.Changes;             // ChangeRecord, ChangeOperation (step 7)
using Papuma.Kernel.Hosting;             // AddPapumaKernel
using Papuma.Kernel.Model;               // SensitiveData (step 2)
using Papuma.Kernel.Processing;          // IChangeHandler (step 7)
using Papuma.Kernel.Store;               // DocumentStore, ConcurrencyException
using Papuma.Kernel.Tenancy;             // ScopeContext, IScopeResolver

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPapumaKernel(o =>
{
    o.ConnectionString =
        "Host=127.0.0.1;Port=5432;Database=papuma_tut;Username=postgres;Password=postgres";
    o.Model(m => m.Document<SupportTicket>());
});

var app = builder.Build();
app.Run();
```

Run it once — `dotnet run` — and the app starts, connects, and creates its
schema. Stop it (Ctrl+C). Nothing is reachable yet; that is next.

> Curious what it built? `\dt papuma.*` in `psql` shows `papuma.document`,
> `papuma.change`, `papuma.event` — the [architecture §4](architecture.md) data
> model, created for you.

---

## 4. Write your first ticket

Two new pieces. First, **scope**: every read and write happens within a tenant
([concepts §23](concepts.md) — isolation is fail-closed). A real app resolves the
tenant per request; for the tutorial we resolve everyone to one demo tenant. Add
the resolver at the bottom of the file:

```csharp
public sealed class DemoScope : IScopeResolver
{
    public ScopeContext Resolve(HttpContext context) => ScopeContext.Tenant("demo");
}
```

Wire it up and add the create endpoint (in `Program.cs`, before `app.Run()`):

```csharp
builder.Services.AddPapumaScope<DemoScope>();   // ← add up with the other services
// …
var app = builder.Build();
app.UseScopeResolution();                       // resolves the scope per request

app.MapPost("/tickets", async (CreateTicket req, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var id = Guid.NewGuid().ToString("N");
    await session.SaveAsync(
        new SupportTicket(id, req.Subject, req.RequesterEmail), expectedVersion: 0);
    await session.CommitAsync();   // nothing is persisted until you commit
    return Results.Created($"/tickets/{id}", new { id });
});

app.Run();

public sealed record CreateTicket(string Subject, string RequesterEmail);
```

The `expectedVersion: 0` says "I expect this to be an insert" — optimistic
concurrency is mandatory on save ([ADR-003](adr/adr-003-write-path-concurrency.md)),
which becomes the point of step 6. The session is a **unit of work**
([architecture §5](architecture.md)): you could save several documents and append
facts, and `CommitAsync` makes them all visible together, atomically.

> **Causation tracking.** In production you would pass `SessionOptions` with
> `ActorId`, `CausationType` and `CausationId` so every change record carries
> who did what and why. The tutorial omits this for brevity — see the
> [causation tracking recipe](recipes/causation-tracking.md) for the full pattern.

```bash
dotnet run &
curl -s -X POST localhost:5000/tickets -H 'content-type: application/json' \
  -d '{"subject":"Cannot log in","requesterEmail":"ada@example.com"}'
# → {"id":"…"}  — copy that id for the next steps
```

---

## 5. Read it back

```csharp
app.MapGet("/tickets/{id}", async (string id, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var ticket = await session.LoadAsync<SupportTicket>(id);
    return ticket is null
        ? Results.NotFound()
        : Results.Ok(new { ticket.Document, ticket.Version });
});
```

```bash
curl -s localhost:5000/tickets/<id>
# → {"document":{"id":"…","subject":"Cannot log in","requesterEmail":"ada@example.com","status":0},"version":1}
```

A `LoadAsync` is a direct, strongly-consistent read of the current document
([concepts §16](concepts.md)) — no projection to maintain for "show me this one
thing". Note `version: 1`: it climbs with every change, and you just got it for
free.

---

## 6. Change it — and meet optimistic concurrency

Resolve the ticket with a **patch**: a single-statement field update that needs
no prior load ([ADR-012](adr/adr-012-partial-updates.md)):

```csharp
app.MapPost("/tickets/{id}/resolve", async (string id, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    await session.PatchAsync<SupportTicket>(id, p => p.Set(x => x.Status, TicketStatus.Resolved));
    await session.CommitAsync();
    return Results.NoContent();
});
```

```bash
curl -s -X POST localhost:5000/tickets/<id>/resolve   # 204; re-GET shows status:1, version:2
```

Now the lesson that makes the kernel safe. When you load-modify-save, you pass
the version you read. If someone else changed the document in between, your save
is rejected — a lost update is **impossible**, not merely unlikely:

```csharp
app.MapPost("/tickets/{id}/subject", async (string id, RenameTicket req, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var current = await session.LoadAsync<SupportTicket>(id);
    if (current is null) return Results.NotFound();
    try
    {
        await session.SaveAsync(
            current.Document with { Subject = req.Subject }, current.Version);
        await session.CommitAsync();
        return Results.NoContent();
    }
    catch (ConcurrencyException ex)
    {
        // Typed: it carries the expected and the actual version.
        return Results.Conflict(new { expected = ex.ExpectedVersion, actual = ex.ActualVersion });
    }
});

public sealed record RenameTicket(string Subject);
```

Add `using Papuma.Kernel.Store;` already covers `ConcurrencyException`. Two
concurrent renames serialize: the second one sees `409` with both versions
instead of silently clobbering the first.

---

## 7. The payoff — a read model the feed builds for you

Here is the moment the architecture earns its name. You have wired **no**
messaging, no outbox, no trigger. Yet every change you committed was recorded as
a derived, ordered feed entry ([concepts §22](concepts.md)). Attach a handler and
it just flows.

We build a live "open tickets" counter as an in-memory read model. First a
singleton to hold it, then a handler that reacts to ticket changes, then an
endpoint to read it. Add the singleton and handler at the bottom:

```csharp
public sealed class OpenTicketStats
{
    private int _open;
    public int Open => Volatile.Read(ref _open);
    public void Add(int delta) => Interlocked.Add(ref _open, delta);
}

public sealed class OpenTicketProjection(OpenTicketStats stats) : IChangeHandler
{
    public string Name => "open-ticket-stats";   // the checkpoint identity — never rename

    public Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != nameof(SupportTicket)) return Task.CompletedTask;

        if (change.Operation == ChangeOperation.Insert)
            stats.Add(+1);                                  // a new ticket is Open
        else if (change.FieldChanged<SupportTicket>(x => x.Status))
            stats.Add(-1);                                  // resolved → leaves the open set

        return Task.CompletedTask;
    }
}
```

Register both and expose the count (with the other services / endpoints):

```csharp
builder.Services.AddSingleton<OpenTicketStats>();
builder.Services.AddPapumaKernel(/* … as before … */)
    .AddChangeHandler<OpenTicketProjection>();   // ← chain this onto AddPapumaKernel

app.MapGet("/stats/open", (OpenTicketStats stats) => Results.Ok(new { open = stats.Open }));
```

```bash
curl -s -X POST localhost:5000/tickets -H 'content-type: application/json' \
  -d '{"subject":"Printer on fire","requesterEmail":"grace@example.com"}'
curl -s localhost:5000/stats/open      # → {"open":1}  (or more, if earlier tickets are still open)
curl -s -X POST localhost:5000/tickets/<id>/resolve
curl -s localhost:5000/stats/open      # → one less — the feed updated the read model
```

You wrote a document; a handler you registered once reacted. That indirection is
the whole point: the same handler shape drives a SQL projection, a search index,
a SignalR push, or a message bus — see [external-read-models](recipes/external-read-models.md).
The engine gives you strict ordering, persisted checkpoints, retry, and rebuild
for free ([ADR-009](adr/adr-009-projections-as-dumb-handlers.md)); your handler
must only be idempotent (here, trivially — see the note below).

> **Honest caveat for the curious.** This in-memory counter resets on restart and
> double-counts under at-least-once redelivery; it is the simplest thing that
> shows the feed working. A real read model stores the document `version`
> alongside its state and ignores anything not newer — the
> [external-read-models recipe](recipes/external-read-models.md) shows the
> idempotent version-guarded upsert. Keep that in mind, but don't let it
> distract from the lesson here.

---

## 8. Look inside the feed — the audit trail, privacy included

Every change is kept with its actor, time, and a reversible field diff — that
*is* your audit log ([ADR-004](adr/adr-004-changerecord-diff-only.md)). And the
`[SensitiveData]` attribute from step 2 now pays off:

```csharp
app.MapGet("/tickets/{id}/history", async (string id, DocumentStore store, HttpContext http) =>
{
    await using var session = store.OpenSession(http.GetScopeContext());
    var history = await session.GetHistoryAsync<SupportTicket>(id);
    return Results.Ok(history.Select(c => new
    {
        c.Version,
        Operation = c.Operation.ToString(),
        c.OccurredAt,
        Diff = c.Diff.ToJson(),
    }));
});
```

```bash
curl -s localhost:5000/tickets/<id>/history
```

In the diff you will see `subject` and `status` with their before/after values —
but `requesterEmail` appears only as `{"changed": true}`. The value never
entered the feed, the handler in step 7 never saw it, and no projection, log, or
AI consumer downstream can leak what they were never given
([concepts §27](concepts.md)). You declared that once, at the field, in step 2.

---

## Where to go next

You now know the core loop: **document → session/commit → derived feed →
handler**, with versioning and privacy built in. Pick your direction:

- **Breadth — every feature as running code.** The
  [shop-minimal-api sample](https://github.com/papumabiz/Papuma.Kernel/tree/master/samples/shop-minimal-api) extends exactly this
  loop with approval workflows, saga compensation, a bounded counter that cannot
  oversell, realtime UI push, the MCP endpoint and the dashboard. Pair it with
  the [recipes](recipes/).
- **An architecture to build *this* way.** If you like vertical slices and Event
  Modeling (Dymitruk/Dilger), the [event-modeled-slices sample](https://github.com/papumabiz/Papuma.Kernel/tree/master/samples/event-modeled-slices)
  and the [slice recipe](recipes/event-modeling-slices.md) show the shape — one
  Command/View/Automation slice each, with infrastructure-free tests. Entirely
  optional; the kernel does not require it.
- **The reasoning.** [concepts.md](concepts.md) is the long-form *why*; the
  [16 ADRs](adr/) are the individual decisions; [gdpr.md](gdpr.md) covers the
  data-subject obligations.

Tear down the tutorial container when done: `docker rm -f papuma-tut`.
