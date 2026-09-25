# Papuma Kernel — Getting Started

Status: verified against the implemented API (2026-06-11) ·
Prerequisite: **PostgreSQL ≥ 18** ([ADR-001](adr/adr-001-postgresql-18-only.md))

> **PostgreSQL kernel only.** With `Papuma.Kernel.Local` (SQLite) the model,
> session and handler API are the same, but bootstrap and engine details differ —
> read the playbook's [Differences section](ai/papuma-kernel-playbook.md#differences-when-using-papumakernellocal-sqlite-embedded) instead of section 2.

The kernel in five minutes: define the model, bootstrap, write, react to changes.
Background in [architecture.md](architecture.md) and [concepts.md](concepts.md).

> Prefer to learn by building? The [tutorial](tutorial.md) walks one small app
> from an empty folder to a feed-driven read model, step by step. This page is
> the terse reference tour.

## 1. Define documents and events

Ordinary C# classes. Convention: a `string Id` property (or `HasId(...)`).
Attributes set privacy defaults at the place of truth (ADR-007):

```csharp
public sealed record User(
    string Id,
    string Name,
    [property: SensitiveData] string Email,     // diff: only {"changed": true}
    [property: TrackHash] string PasswordHash,  // diff: {"changed": true, "hash": "…"}
    [property: DoNotTrack] string? LastSeen,    // never appears in the feed
    string Status = "active",
    int LoginCount = 0,
    Address? Address = null);                   // nested → recursive diff

public sealed record Address(string City, string Zip);

public sealed record UserLoggedIn(string UserId, string Device,
    [property: SensitiveData] string? Ip = null);   // fact without state (ADR-013)
```

## 2. Bootstrap

```csharp
builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("papuma");
        o.Model(m => m
            .Document<User>(d => d
                .UniqueKey(x => x.Email))               // partial expression index (ADR-006)
            .Event<UserLoggedIn>(e => e
                .Retention(TimeSpan.FromDays(90))));    // opt-in purging (ADR-013)
    })
    .AddChangeHandler<UserProjection>()      // ADR-009: dumb handlers
    .AddEventHandler<LoginAuditHandler>();   // ADR-013: event consumption
```

The bootstrap registers `DocumentStore` + `KernelModel`, creates the schema
idempotently at startup (tables, RLS, key indexes) and hosts the feed workers
(NOTIFY-driven, polling as the truth) as well as the retention worker.

Keys are always scoped to the tenant. For "unique within a parent" (number per
project), declare a **composite key** — `.UniqueKey(x => new { x.ProjectId, x.Number })`
— and look it up with one value per component:
`LoadByKeyAsync<Ticket>(x => new { x.ProjectId, x.Number }, ["p1", 42])` (ADR-020).
Like single-field keys, it is enforced only for documents that carry every
component.

## 3. Writing — the session as unit of work

```csharp
await using var session = store.OpenSession(ScopeContext.Tenant("acme"),
    new SessionOptions { ActorId = userId, CausationType = "RegisterUser" });

// Registration: document + fact, one atomic commit, shared correlationId
await session.SaveAsync(user, expectedVersion: 0);          // 0 = insert expected
await session.AppendAsync(new UserLoggedIn(user.Id, "web"));
await session.CommitAsync();                                 // without commit: rollback

// Optimistic concurrency is mandatory on save (ADR-003):
var loaded = await session.LoadAsync<User>(user.Id);
await session.SaveAsync(loaded.Document with { Name = "Harald" }, loaded.Version);

// Patch: single fields without loading, one statement (ADR-012)
var patched = await session.PatchAsync<User>(user.Id, p => p
    .Set(x => x.Name, "Harry")
    .Increment(x => x.LoginCount));
var loginCount = patched.GetDocument<User>().LoginCount;   // the stored state, no second read

// Bulk: the same patch across declared keys or id lists (ADR-014)
await session.PatchWhereAsync<User>(x => x.Status, "inactive",
    p => p.Set(x => x.Status, "archived"));

// Rollback: append-only back to an earlier state (ADR-008)
await session.RollbackAsync<User>(user.Id, toVersion: 1, expectedVersion: 3);
await session.CommitAsync();
```

A tenant id matches `^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$` — 2–101 letters, digits,
`_` or `-`, starting with a letter or digit; GUID strings fit. Ids compare
case-sensitively, so for GUIDs use `ScopeContext.Tenant(Guid)`: it always yields
the lowercase dashed form, and every part of the application stores the same shape.

Conflicts are typed (`ConcurrencyException` with expected and actual version,
`UniqueKeyViolationException` with the key path, …) and local thanks to
savepoints: a failure does not discard earlier session writes.

## 4. Reacting — change handlers

```csharp
public sealed class UserProjection : IChangeHandler
{
    public string Name => "user-projection";

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != "User") return;

        if (change.FieldChanged<User>(x => x.Email))
        {
            // change.Diff is policy-applied — sensitive values never reach handlers
        }

        // Your own SQL, Elasticsearch, webhook — the kernel generates nothing (ADR-009)
    }
}
```

The engine guarantees: strict `seq` ordering per handler, persisted checkpoints,
retry with backoff, poison skip, at-least-once (handlers must be idempotent —
natural key: handler name + `change.Seq`). Rebuild:
`ChangeFeedProcessor.ResetCheckpointAsync(name)`.

## 5. Schema evolution

```csharp
o.Model(m => m.Document<User>(d => d
    .Upcast(1, json => { json["email"] = json["mail"]; json.Remove("mail"); })));
```

Additive changes (a new optional field, a removed field) need **no** upcaster
(ADR-005). Upcasting happens lazily at load time; persistence follows on the next
save.

## 6. Operations

```csharp
builder.Services.AddHealthChecks().AddPapumaChangeFeedLag(maxAllowedLag: 1000);
app.UseScopeResolution();   // tenant middleware (implement IScopeResolver)
```

## 7. Without a host (tests, tools, console apps)

`AddPapumaKernel` is a convenience, not a requirement. The same store, built by
hand:

```csharp
await using var dataSource = NpgsqlDataSource.Create(connectionString);

var model = new KernelModelBuilder()
    .Document<User>(d => d.UniqueKey(x => x.Email))
    .Event<UserLoggedIn>()
    .Build();

await SchemaManager.EnsureSchemaAsync(dataSource, model);   // idempotent; pass the model, or no key indexes
var store = new DocumentStore(dataSource, model);

// Feed handlers without the hosted worker: one cycle on demand
using var processor = new ChangeFeedProcessor(dataSource, [new UserProjection()]);
await processor.ProcessOnceAsync();
```

For integration tests, the kernel's own suite uses this shape
(`tests/Papuma.Kernel.Tests/Infrastructure/PostgresFixture.cs` in the repo):

- **One `postgres:18` container per test run** (Testcontainers), shared by all
  tests; `EnsureSchemaAsync` is idempotent, so every test class may call it.
- **A fresh tenant per test** — `ScopeContext.Tenant(Guid.NewGuid())` —
  isolates tests without truncating tables.
- **Connect as a non-superuser role** when a test asserts isolation: superusers
  bypass row-level security, so a superuser connection hides RLS bugs. Grant the
  role access to the `papuma` schema after `EnsureSchemaAsync`.
- **Feed handlers see every tenant.** In a shared database, give each test's
  handler a unique `Name` (its own checkpoint) and filter on `change.Scope`, or
  the handler also receives the other tests' changes.

## Further reading

[recipe: causation tracking](recipes/causation-tracking.md) ·
[recipe: realtime UI notifications](recipes/realtime-ui-notifications.md) ·
ADRs in [adr/](adr/)
