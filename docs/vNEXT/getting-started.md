# Papuma vNEXT — Getting Started

Status: verified against the implemented API (2026-06-11) ·
Prerequisite: **PostgreSQL ≥ 18** ([ADR-001](adr/adr-001-postgresql-18-only.md))

The kernel in five minutes: define the model, bootstrap, write, react to changes.
Background in [architecture.md](architecture.md) and [concepts.md](concepts.md).

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

## 3. Writing — the session as unit of work

```csharp
await using var session = store.OpenSession(ScopeContext.Tenant("acme"));

// Registration: document + fact, one atomic commit, shared correlationId
await session.SaveAsync(user, expectedVersion: 0);          // 0 = insert expected
await session.AppendAsync(new UserLoggedIn(user.Id, "web"));
await session.CommitAsync();                                 // without commit: rollback

// Optimistic concurrency is mandatory on save (ADR-003):
var loaded = await session.LoadAsync<User>(user.Id);
await session.SaveAsync(loaded.Document with { Name = "Harald" }, loaded.Version);

// Patch: single fields without loading, one statement (ADR-012)
await session.PatchAsync<User>(user.Id, p => p
    .Set(x => x.Name, "Harry")
    .Increment(x => x.LoginCount));

// Bulk: the same patch across declared keys or id lists (ADR-014)
await session.PatchWhereAsync<User>(x => x.Status, "inactive",
    p => p.Set(x => x.Status, "archived"));

// Rollback: append-only back to an earlier state (ADR-008)
await session.RollbackAsync<User>(user.Id, toVersion: 1, expectedVersion: 3);
await session.CommitAsync();
```

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

Further reading: [recipe: realtime UI notifications](recipes/realtime-ui-notifications.md) ·
[implementation plan](implementation-plan.md) · ADRs in [adr/](adr/)
