# Papuma vNEXT — Getting Started

Status: Verifiziert gegen die implementierte API (2026-06-11) ·
Voraussetzung: **PostgreSQL ≥ 18** ([ADR-001](adr/adr-001-postgresql-18-only.md))

Der Kernel in fünf Minuten: Modell definieren, bootstrappen, schreiben, auf
Änderungen reagieren. Hintergründe in [architecture.md](architecture.md) und
[concepts.md](concepts.md).

## 1. Dokumente und Events definieren

Gewöhnliche C#-Klassen. Konvention: eine `string Id`-Property (oder `HasId(...)`).
Attribute setzen Datenschutz-Defaults am Ort der Wahrheit (ADR-007):

```csharp
public sealed record User(
    string Id,
    string Name,
    [property: SensitiveData] string Email,     // Diff: nur {"changed": true}
    [property: TrackHash] string PasswordHash,  // Diff: {"changed": true, "hash": "…"}
    [property: DoNotTrack] string? LastSeen,    // erscheint nie im Feed
    string Status = "active",
    int LoginCount = 0,
    Address? Address = null);                   // verschachtelt → rekursives Diff

public sealed record Address(string City, string Zip);

public sealed record UserLoggedIn(string UserId, string Device,
    [property: SensitiveData] string? Ip = null);   // Faktum ohne Zustand (ADR-013)
```

## 2. Bootstrap

```csharp
builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("papuma");
        o.Model(m => m
            .Document<User>(d => d
                .UniqueKey(x => x.Email))               // partieller Expression-Index (ADR-006)
            .Event<UserLoggedIn>(e => e
                .Retention(TimeSpan.FromDays(90))));    // opt-in Löschung (ADR-013)
    })
    .AddChangeHandler<UserProjection>()      // ADR-009: dumme Handler
    .AddEventHandler<LoginAuditHandler>();   // ADR-013: Event-Konsum
```

Der Bootstrap registriert `DocumentStore` + `KernelModel`, legt beim Start das
Schema idempotent an (Tabellen, RLS, Key-Indizes) und hostet die Feed-Worker
(NOTIFY-getrieben, Polling als Wahrheit) sowie den Retention-Worker.

## 3. Schreiben — Session als Unit of Work

```csharp
await using var session = store.OpenSession(ScopeContext.Tenant("acme"));

// Registrierung: Dokument + Faktum, ein atomarer Commit, gemeinsame correlationId
await session.SaveAsync(user, expectedVersion: 0);          // 0 = Insert erwartet
await session.AppendAsync(new UserLoggedIn(user.Id, "web"));
await session.CommitAsync();                                 // ohne Commit: Rollback

// Optimistische Concurrency ist Pflicht beim Save (ADR-003):
var loaded = await session.LoadAsync<User>(user.Id);
await session.SaveAsync(loaded.Document with { Name = "Harald" }, loaded.Version);

// Patch: Einzelfelder ohne Laden, ein Statement (ADR-012)
await session.PatchAsync<User>(user.Id, p => p
    .Set(x => x.Name, "Harry")
    .Increment(x => x.LoginCount));

// Bulk: derselbe Patch über deklarierte Keys oder ID-Listen (ADR-014)
await session.PatchWhereAsync<User>(x => x.Status, "inactive",
    p => p.Set(x => x.Status, "archived"));

// Rollback: append-only zurück zu einem früheren Stand (ADR-008)
await session.RollbackAsync<User>(user.Id, toVersion: 1, expectedVersion: 3);
await session.CommitAsync();
```

Konflikte sind typisiert (`ConcurrencyException` mit erwarteter und tatsächlicher
Version, `UniqueKeyViolationException` mit Key-Pfad, …) und dank Savepoints lokal:
Ein Fehlschlag verwirft frühere Session-Writes nicht.

## 4. Reagieren — Change Handler

```csharp
public sealed class UserProjection : IChangeHandler
{
    public string Name => "user-projection";

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        if (change.DocumentType != "User") return;

        if (change.FieldChanged<User>(x => x.Email))
        {
            // change.Diff: policy-bereinigt — sensible Werte erreichen Handler nie
        }

        // Eigenes SQL, Elasticsearch, Webhook — der Kernel generiert nichts (ADR-009)
    }
}
```

Die Engine garantiert: strikte `seq`-Ordnung pro Handler, persistierte Checkpoints,
Retry mit Backoff, Poison-Skip, At-least-once (Handler müssen idempotent sein —
natürlicher Schlüssel: Handler-Name + `change.Seq`). Rebuild:
`ChangeFeedProcessor.ResetCheckpointAsync(name)`.

## 5. Schema-Evolution

```csharp
o.Model(m => m.Document<User>(d => d
    .Upcast(1, json => { json["email"] = json["mail"]; json.Remove("mail"); })));
```

Additive Änderungen (neues optionales Feld, Feld entfernt) brauchen **keinen**
Upcaster (ADR-005). Upcasting passiert lazy beim Laden; persistiert wird beim
nächsten Save.

## 6. Betrieb

```csharp
builder.Services.AddHealthChecks().AddPapumaChangeFeedLag(maxAllowedLag: 1000);
app.UseScopeResolution();   // Tenant-Middleware (IScopeResolver implementieren)
```

Weiterführend: [Rezept Echtzeit-UI-Benachrichtigungen](recipes/realtime-ui-notifications.md) ·
[Implementierungsplan](implementation-plan.md) · ADRs in [adr/](adr/)
