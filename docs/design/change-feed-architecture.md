# Papuma Change-Feed DSL – Architektur & Implementierungsspezifikation

**Ziel:** Eine deklarative, typsichere DSL-Schicht über dem existierenden `ChangeWriter`, die das Schreiben von Change-Feed-Einträgen ergonomisch, testbar und explizit macht – ohne ORM-Magie, ohne Trigger, ohne Diffing-Engine.

**Package:** `Papuma.Kernel.ChangeFeed.Dsl` (Extension-Package, referenziert `Papuma.Kernel`)

---

## 1. Architekturüberblick

Die DSL-Schicht sitzt **zwischen** dem Use-Case-Handler und dem existierenden `ChangeWriter`:

```
Use Case Handler
     │
     ├─ CRUD (SQL, Dapper, EF) → Domain-Tabellen
     │
     └─ ChangeKernel.Process(entity, operation)
            │
            ├─ ChangeContract<T> #1 ──→ ChangeEvent #1
            ├─ ChangeContract<T> #2 ──→ ChangeEvent #2
            └─ ...
                  │
                  ▼
          ChangeWriter.AppendChangeAsync(...)    ← existierender Kernel
                  │
                  ▼
          papuma_event_feed (atomar in derselben TX)
```

**Grundprinzip:** Die DSL definiert _was_ relevant ist, nicht _wie_ sich Daten geändert haben. Der `ChangeWriter` des Kernels bleibt die einzige Schreibschnittstelle zur `papuma_event_feed`.

---

## 2. Kern-Datenmodelle

### 2.1 `ChangeOperation` – Die Art der Mutation

> **Hinweis:** `ChangeOperation` ist ein reines DSL-Konzept für die Rule-Evaluation. Es wird **nicht** in der `papuma_event_feed`-Tabelle persistiert. Die Art der Mutation fließt stattdessen in den `eventType`-String ein (z.B. `"UserUpdated"`, `"UserDeleted"`). Die Trennung ist bewusst: Die DSL entscheidet _ob_ ein Event erzeugt wird, der `eventType` beschreibt _was_ passiert ist.

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Describes the kind of mutation that produced a change.
/// This is a pure DSL concept used for rule evaluation; it is not persisted in the event feed.
/// </summary>
public enum ChangeOperation
{
    /// <summary>The entity was newly created.</summary>
    Insert,
    /// <summary>An existing entity was modified.</summary>
    Update,
    /// <summary>The entity was deleted.</summary>
    Delete
}
```

### 2.2 `ChangeContext<T>` – Der Kontext einer Änderung

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Carries the state and metadata of a mutation for rule evaluation.
/// </summary>
/// <typeparam name="T">The domain entity type.</typeparam>
public sealed record ChangeContext<T>
{
    /// <summary>The current (post-mutation) entity state.</summary>
    public required T Entity { get; init; }

    /// <summary>The kind of mutation that occurred.</summary>
    public required ChangeOperation Operation { get; init; }

    /// <summary>
    /// The entity state before the mutation.
    /// Set this when the old state is available and useful for diff-based rules.
    /// </summary>
    public T? Before { get; init; }

    /// <summary>
    /// Optional actor identifier. When set, overrides the default actor on <see cref="ChangeKernel"/>.
    /// </summary>
    public string? ActorId { get; init; }

    /// <summary>
    /// Optional correlation identifier for distributed tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional idempotency key for deduplication.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}
```

### 2.3 `ChangeContract` – Die DSL-Regeldefinition

Statt der vNEXT-`ChangeEvent`-Typen nutzen wir die existierenden `ChangeRecord`-Daten des Kernels **nicht** direkt auf DSL-Ebene. Die DSL erzeugt ihre eigenen, leichtgewichtigen Event-DTOs, die der `ChangeKernel` dann in `ChangeWriter`-Aufrufe übersetzt.

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Repräsentiert ein von der DSL erzeugtes Change-Event.
/// Wird vom <see cref="ChangeKernel"/> in <c>ChangeWriter.AppendChangeAsync</c>-Aufrufe übersetzt.
/// </summary>
public sealed record DslChangeEvent
{
    /// <summary>Logischer Stream-Name (z.B. "orders", "users").</summary>
    public required string Stream { get; init; }

    /// <summary>Event-Typ (z.B. "UserEmailChanged").</summary>
    public required string Type { get; init; }

    /// <summary>ID der betroffenen Entität.</summary>
    public required string EntityId { get; init; }

    /// <summary>Serialisierte JSON-Payload.</summary>
    public string? PayloadJson { get; init; }

    /// <summary>Payload-Schema-Version.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Optionaler Causation-Identifier.</summary>
    public string? CausationId { get; init; }
}
```

---

## 3. Regel-Schnittstelle (`IChangeRule<T>`)

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Evaluates whether a change context matches this rule and, if so,
/// produces zero or more change events for the outbox.
/// </summary>
/// <typeparam name="T">The domain entity type this rule handles.</typeparam>
public interface IChangeRule<T>
{
    /// <summary>
    /// Returns <c>true</c> when this rule should fire for the given context.
    /// </summary>
    bool Matches(ChangeContext<T> ctx);

    /// <summary>
    /// Produces the change events to append to the <c>papuma_event_feed</c>.
    /// Called only when <see cref="Matches"/> returned <c>true</c>.
    /// </summary>
    IEnumerable<DslChangeEvent> Evaluate(ChangeContext<T> ctx);
}
```

---

## 4. Fluent Rule Builder (`ChangeRule<T>`)

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Fluent builder for defining a change rule.
/// </summary>
/// <typeparam name="T">The domain entity type.</typeparam>
public sealed class ChangeRule<T> : IChangeRule<T>
{
    private Func<ChangeContext<T>, bool> _when = _ => true;
    private Func<ChangeContext<T>, IEnumerable<DslChangeEvent>> _map = _ => [];

    /// <summary>
    /// Sets a predicate that must return <c>true</c> for the rule to fire.
    /// </summary>
    public ChangeRule<T> When(Func<ChangeContext<T>, bool> predicate)
    {
        _when = predicate;
        return this;
    }

    /// <summary>
    /// Defines a single event to emit when the rule fires.
    /// </summary>
    public ChangeRule<T> Emit(Func<ChangeContext<T>, DslChangeEvent> map)
    {
        _map = ctx => [map(ctx)];
        return this;
    }

    /// <summary>
    /// Defines multiple events to emit when the rule fires.
    /// </summary>
    public ChangeRule<T> EmitMany(Func<ChangeContext<T>, IEnumerable<DslChangeEvent>> map)
    {
        _map = map;
        return this;
    }

    /// <inheritdoc />
    public bool Matches(ChangeContext<T> ctx) => _when(ctx);

    /// <inheritdoc />
    public IEnumerable<DslChangeEvent> Evaluate(ChangeContext<T> ctx) => _map(ctx);
}
```

---

## 5. ChangeKernel – Die zentrale Koordinationsstelle

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Central coordinator that evaluates registered rules against change contexts
/// and writes matching events to the unified event feed via <see cref="ChangeWriter"/>.
/// </summary>
public sealed class ChangeKernel
{
    private readonly ChangeWriter _changeWriter;
    private readonly List<object> _rules = []; // stored as object; filtered by type in ProcessAsync

    /// <summary>
    /// Gets or sets the default actor identifier used when no actor is supplied on the context.
    /// </summary>
    public string DefaultActorId { get; set; } = "system";

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeKernel"/> class.
    /// </summary>
    /// <param name="changeWriter">The kernel's change feed writer.</param>
    public ChangeKernel(ChangeWriter changeWriter)
    {
        ArgumentNullException.ThrowIfNull(changeWriter);
        _changeWriter = changeWriter;
    }

    /// <summary>
    /// Registers a rule for a specific entity type.
    /// <para>
    /// <b>Thread-Safety:</b> All rules must be registered during application startup
    /// (composition root) before the first call to <see cref="ProcessAsync{T}"/>.
    /// The rule list is not synchronized for concurrent writes. After startup,
    /// the list is effectively read-only and safe for concurrent <c>ProcessAsync</c> calls.
    /// </para>
    /// </summary>
    public void Register<T>(IChangeRule<T> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add(rule);
    }

    /// <summary>
    /// Processes a change context against all registered rules for type <typeparamref name="T"/>.
    /// Must be called within an open <see cref="NpgsqlTransaction"/>.
    /// </summary>
    /// <typeparam name="T">The domain entity type.</typeparam>
    /// <param name="ctx">The change context.</param>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="scope">The scope context.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task ProcessAsync<T>(
        ChangeContext<T> ctx,
        NpgsqlTransaction transaction,
        ScopeContext scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(scope);

        var actorId = ctx.ActorId ?? DefaultActorId;

        foreach (var rule in _rules.OfType<IChangeRule<T>>())
        {
            if (!rule.Matches(ctx))
                continue;

            foreach (var evt in rule.Evaluate(ctx))
            {
                await _changeWriter.AppendChangeAsync(
                    transaction: transaction,
                    scope: scope,
                    entity: evt.Stream,
                    entityId: evt.EntityId,
                    eventType: evt.Type,
                    version: evt.Version,
                    payloadJson: evt.PayloadJson ?? "{}",
                    actorId: actorId,
                    correlationId: ctx.CorrelationId,
                    causationId: evt.CausationId,
                    idempotencyKey: ctx.IdempotencyKey,
                    ct: ct);
            }
        }
    }

    /// <summary>
    /// Creates a new change context for the given entity and operation.
    /// Convenience method – the context can also be created manually.
    /// </summary>
    public static ChangeContext<T> Context<T>(
        T entity,
        ChangeOperation operation,
        T? before = default,
        string? actorId = null,
        string? correlationId = null,
        string? idempotencyKey = null)
        where T : notnull
        => new()
        {
            Entity = entity,
            Operation = operation,
            Before = before,
            ActorId = actorId,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey
        };
}
```

---

## 6. Convenience-Integration: `ChangeAwareDb`

Für Use Cases, die SQL und Change-Feed in einer Transaktion ausführen wollen, ohne manuell Transaction-Handling zu betreiben:

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Convenience wrapper that runs SQL mutations and change-feed processing
/// inside a single database transaction via <see cref="IUnitOfWork"/>.
/// </summary>
public sealed class ChangeAwareDb
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ChangeKernel _kernel;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeAwareDb"/> class.
    /// </summary>
    public ChangeAwareDb(IUnitOfWork unitOfWork, ChangeKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(kernel);

        _unitOfWork = unitOfWork;
        _kernel = kernel;
    }

    /// <summary>
    /// Executes SQL mutations and change-feed processing within a single transaction.
    /// The <paramref name="scope"/> is passed per call because it is request-scoped
    /// (e.g. resolved from <c>ScopeMiddleware</c>) and must not be captured at registration time.
    /// </summary>
    /// <typeparam name="T">The domain entity type.</typeparam>
    /// <param name="mutations">The SQL mutations to execute (receives connection + transaction).</param>
    /// <param name="ctx">The change context to process after the mutations succeed.</param>
    /// <param name="scope">The request-scoped scope context (platform or tenant).</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task ExecuteAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> mutations,
        ChangeContext<T> ctx,
        ScopeContext scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await _unitOfWork.ExecuteAsync(async (conn, tx, ct) =>
        {
            await mutations(conn, tx, ct);
            await _kernel.ProcessAsync(ctx, tx, scope, ct);
        }, ct);
    }
}
```

---

## 7. Extension Methods für DI-Registrierung

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering the DSL layer in the DI container.
/// </summary>
public static class ChangeFeedDslExtensions
{
    /// <summary>
    /// Registers <see cref="ChangeKernel"/> as a singleton service.
    /// Requires that <see cref="ChangeWriter"/> is already registered (via <c>AddPapumaKernel</c>).
    /// </summary>
    public static IServiceCollection AddChangeFeedDsl(
        this IServiceCollection services,
        Action<ChangeFeedDslOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ChangeFeedDslOptions();
        configure?.Invoke(options);

        services.AddSingleton(sp =>
        {
            var kernel = new ChangeKernel(sp.GetRequiredService<ChangeWriter>())
            {
                DefaultActorId = options.DefaultActorId
            };
            return kernel;
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="ChangeAwareDb"/> as a scoped service.
    /// <c>ScopeContext</c> is not captured here because it is request-scoped;
    /// instead it is passed per call to <see cref="ChangeAwareDb.ExecuteAsync{T}"/>.
    /// </summary>
    public static IServiceCollection AddChangeAwareDb(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped(sp => new ChangeAwareDb(
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<ChangeKernel>()));

        return services;
    }
}

/// <summary>
/// Configuration options for the Change Feed DSL layer.
/// </summary>
public sealed class ChangeFeedDslOptions
{
    /// <summary>
    /// Default actor identifier used when no actor is supplied in the change context.
    /// </summary>
    public string DefaultActorId { get; set; } = "system";
}
```

---

## 8. Vollständiges Beispiel: User Domain

### 8.1 Regeldefinition (Startup / Composition Root)

```csharp
// Program.cs oder ein Modul-Registrierungspunkt
var kernel = services.GetRequiredService<ChangeKernel>();

// Regel 1: UserUpdated – bei jedem Update
kernel.Register(
    new ChangeRule<User>()
        .When(ctx => ctx.Operation == ChangeOperation.Update)
        .Emit(ctx => new DslChangeEvent
        {
            Stream = "users",
            Type = "UserUpdated",
            EntityId = ctx.Entity.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                ctx.Entity.Id,
                ctx.Entity.Name,
                ctx.Entity.Email
            }),
            Version = 1
        }));

// Regel 2: UserEmailVerified – nur wenn E-Mail verifiziert wurde
kernel.Register(
    new ChangeRule<User>()
        .When(ctx => ctx.Operation == ChangeOperation.Update
                     && ctx.Entity.EmailVerified
                     && (ctx.Before == null || !ctx.Before.EmailVerified))
        .Emit(ctx => new DslChangeEvent
        {
            Stream = "users",
            Type = "UserEmailVerified",
            EntityId = ctx.Entity.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                ctx.Entity.Id,
                ctx.Entity.Email
            }),
            Version = 1
        }));
```

### 8.2 Use-Case-Handler (mit `ChangeAwareDb`)

```csharp
public class UpdateUserEmailHandler
{
    private readonly ChangeAwareDb _db;
    private readonly ScopeContext _scope; // injected per request (e.g. from ScopeMiddleware)

    public UpdateUserEmailHandler(ChangeAwareDb db, ScopeContext scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task HandleAsync(Guid userId, string newEmail, CancellationToken ct)
    {
        var before = await LoadUserAsync(userId, ct);

        await _db.ExecuteAsync(
            mutations: async (conn, tx, ct) =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE users SET email = @email, updated_at = NOW()
                    WHERE id = @id
                    """;
                cmd.Parameters.AddWithValue("email", newEmail);
                cmd.Parameters.AddWithValue("id", userId);
                await cmd.ExecuteNonQueryAsync(ct);
            },
            ctx: ChangeKernel.Context(
                entity: before! with { Email = newEmail },
                operation: ChangeOperation.Update,
                before: before,
                actorId: "user:550e8400-...",
                correlationId: Activity.Current?.Id),
            scope: _scope,
            ct: ct);
    }
}
```

### 8.3 Use-Case-Handler (manuell, ohne `ChangeAwareDb`)

```csharp
public class UpdateUserEmailHandler
{
    private readonly IUnitOfWork _uow;
    private readonly ChangeKernel _kernel;
    private readonly ScopeContext _scope;

    public UpdateUserEmailHandler(IUnitOfWork uow, ChangeKernel kernel, ScopeContext scope)
    {
        _uow = uow;
        _kernel = kernel;
        _scope = scope;
    }

    public async Task HandleAsync(Guid userId, string newEmail, CancellationToken ct)
    {
        var before = await LoadUserAsync(userId, ct);

        await _uow.ExecuteAsync(async (conn, tx, ct) =>
        {
            // CRUD
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE users SET email = @email, updated_at = NOW() WHERE id = @id";
            cmd.Parameters.AddWithValue("email", newEmail);
            cmd.Parameters.AddWithValue("id", userId);
            await cmd.ExecuteNonQueryAsync(ct);

            // Change Feed DSL
            await _kernel.ProcessAsync(
                ChangeKernel.Context(
                    entity: before! with { Email = newEmail },
                    operation: ChangeOperation.Update,
                    before: before,
                    actorId: "user:550e8400-..."),
                tx, _scope, ct);
        }, ct);
    }
}
```

---

## 9. Erweiterung: Typisierte Payloads (kein manuelles JSON)

Statt rohem `JsonSerializer.Serialize` können `IVersionedPayload`-Implementierungen die Serialisierung kapseln:

```csharp
namespace Papuma.Kernel.ChangeFeed.Dsl;

/// <summary>
/// Typed payload that knows its own version.
/// </summary>
public interface IVersionedPayload
{
    int Version { get; }
}

public record UserUpdatedPayload(Guid Id, string Name, string Email) : IVersionedPayload
{
    public int Version => 1;
}

public record UserEmailVerifiedPayload(Guid Id, string Email) : IVersionedPayload
{
    public int Version => 1;
}

public static class ChangeRuleExtensions
{
    /// <summary>
    /// Emits a single event with a typed, versioned payload.
    /// </summary>
    public static ChangeRule<T> Emit<T, TPayload>(
        this ChangeRule<T> rule,
        string stream,
        string eventType,
        Func<ChangeContext<T>, (string EntityId, TPayload Payload)> map)
        where TPayload : IVersionedPayload
    {
        return rule.Emit(ctx =>
        {
            var (entityId, payload) = map(ctx);
            return new DslChangeEvent
            {
                Stream = stream,
                Type = eventType,
                EntityId = entityId,
                PayloadJson = JsonSerializer.Serialize(payload),
                Version = payload.Version
            };
        });
    }
}
```

Nutzung:
```csharp
kernel.Register(
    new ChangeRule<User>()
        .When(ctx => ctx.Operation == ChangeOperation.Update)
        .Emit<User, UserUpdatedPayload>(
            stream: "users",
            eventType: "UserUpdated",
            map: ctx => (ctx.Entity.Id.ToString(), new UserUpdatedPayload(
                ctx.Entity.Id, ctx.Entity.Name, ctx.Entity.Email))));
```

---

## 10. Projektstruktur

```
src/
├── Papuma.Kernel/                          ← existierend (unverändert)
│   ├── ChangeFeed/
│   │   ├── ChangeWriter.cs
│   │   ├── ChangeFeedReader.cs
│   │   ├── ChangeRecord.cs
│   │   └── ...
│   ├── Events/
│   ├── Projections/
│   └── ...
│
└── Papuma.Kernel.ChangeFeed.Dsl/           ← NEU: Extension-Package
    ├── Papuma.Kernel.ChangeFeed.Dsl.csproj
    ├── ChangeOperation.cs
    ├── ChangeContext.cs
    ├── DslChangeEvent.cs
    ├── IChangeRule.cs
    ├── ChangeRule.cs
    ├── ChangeKernel.cs
    ├── ChangeAwareDb.cs
    ├── ChangeFeedDslExtensions.cs
    ├── ChangeFeedDslOptions.cs
    ├── IVersionedPayload.cs
    └── ChangeRuleExtensions.cs
```

**Projektdatei:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>Papuma.Kernel.ChangeFeed.Dsl</PackageId>
    <Description>
      Declarative, type-safe DSL layer for the Papuma.Kernel change feed.
      Provides fluent rule definitions on top of the existing ChangeWriter.
    </Description>
    <PackageTags>postgresql;events;change-feed;dsl</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Papuma.Kernel\Papuma.Kernel.csproj" />
  </ItemGroup>
</Project>
```

---

## 11. Datenbank – Keine Änderungen nötig

Die DSL-Schicht nutzt die existierende `papuma_event_feed`-Tabelle des Kernels. **Keine neuen Tabellen, keine Migrationen erforderlich.** Das ist ein zentraler Vorteil: Die DSL ist eine reine Applikationsschicht.

---

## 12. Fehlerbehandlung & Edge Cases

| Fall | Verhalten |
|---|---|
| Keine Regel matched | `ProcessAsync` ist ein No-Op (kein Fehler) |
| `PayloadJson` ist `null` | Es wird `"{}"` gesetzt (leerer JSON-Objekt) |
| `Before` ist `null` bei `When()` mit Before-Zugriff | `NullReferenceException` – Rule-Autor muss prüfen |
| Idempotency-Key-Kollision | `ChangeWriter` fängt `UniqueViolation` und ignoriert stumm (existierendes Verhalten) |
| Typserialisierungsfehler in `Emit()` | Exception propagiert → Transaktion rollback |
| `actorId` nicht gesetzt in Context | `DefaultActorId` des Kernels wird verwendet |

---

## 13. Teststrategie

### 13.1 Unit-Tests für Rules

```csharp
[Fact]
public void Rule_Matches_When_EmailVerified_Changed()
{
    var rule = new ChangeRule<User>()
        .When(ctx => ctx.Entity.EmailVerified && (ctx.Before == null || !ctx.Before.EmailVerified))
        .Emit(ctx => new DslChangeEvent { Stream = "users", Type = "UserEmailVerified", EntityId = ctx.Entity.Id.ToString() });

    var before = new User { Id = Guid.NewGuid(), Email = "a@b.com", EmailVerified = false };
    var after = before with { EmailVerified = true };

    var ctx = new ChangeContext<User> { Entity = after, Operation = ChangeOperation.Update, Before = before };

    Assert.True(rule.Matches(ctx));
    Assert.Single(rule.Evaluate(ctx));
    Assert.Equal("UserEmailVerified", rule.Evaluate(ctx).First().Type);
}

[Fact]
public void Rule_DoesNotMatch_When_EmailAlreadyVerified()
{
    var rule = new ChangeRule<User>()
        .When(ctx => ctx.Entity.EmailVerified && (ctx.Before == null || !ctx.Before.EmailVerified))
        .Emit(ctx => new DslChangeEvent { Stream = "users", Type = "UserEmailVerified", EntityId = ctx.Entity.Id.ToString() });

    var user = new User { Id = Guid.NewGuid(), Email = "a@b.com", EmailVerified = true };
    var ctx = new ChangeContext<User> { Entity = user, Operation = ChangeOperation.Update, Before = user };

    Assert.False(rule.Matches(ctx));
}
```

### 13.2 Integrations-Tests für ChangeKernel

- `ChangeKernel` mit gemocktem `ChangeWriter` testen
- Verifizieren, dass die richtigen Parameter an `AppendChangeAsync` übergeben werden
- Mehrere Regeln für denselben Typ testen (alle matchenden feuern)
- `DefaultActorId`-Fallback testen

---

## 14. Migrationspfad von bestehendem Code

Die DSL-Schicht ist **optional und koexistierend**. Bestehender Code, der direkt `ChangeWriter.AppendChangeAsync` aufruft, bleibt unverändert gültig.

**Empfohlene Adoptionsstrategie:**
1. DSL-Package in der Solution referenzieren
2. `AddChangeFeedDsl()` in der DI-Registrierung aufrufen
3. Neue Use Cases schreiben Regeln und nutzen `ChangeKernel.ProcessAsync`
4. Bestehende Use Cases können bei Bedarf umgestellt werden (kein Zeitdruck)
5. Gemeinsame Payload-Typen in ein Shared-Projekt extrahieren, wenn sie von mehreren Modulen genutzt werden

---

## 15. Implementierungs-Phasen

### Phase 1: Core DSL (≈ 1 Tag)
1. `ChangeOperation`, `ChangeContext<T>`, `DslChangeEvent`
2. `IChangeRule<T>`, `ChangeRule<T>`
3. `ChangeKernel` mit `ProcessAsync`
4. Extension Methods (`AddChangeFeedDsl`)
5. Unit-Tests für `ChangeRule<T>` und `ChangeKernel`

### Phase 2: Convenience & Typisierung
1. `ChangeAwareDb` als Transaktions-Wrapper
2. `IVersionedPayload` + `ChangeRuleExtensions.Emit<T, TPayload>()`
3. `AddChangeAwareDb` Extension
4. Integrationstests mit `IUnitOfWork`-Mock

### Phase 3: Produktionsreife
1. XML-Dokumentation für alle öffentlichen Typen
2. Performance-Validierung (Regeln werden bei `ProcessAsync` durchiteriert – kein Hot-Path)
3. README & Migrations-Guide
4. NuGet-Paketierung

---

## 16. Abgrenzung: Change vs. Event (`kind`)

Der existierende `ChangeWriter` unterscheidet zwei Arten von Feed-Einträgen:

| `kind` | Methode | Semantik |
|---|---|---|
| `"Change"` | `AppendChangeAsync` | State-Mutation mit `entity`, `entityId`, `version` (alle required) |
| `"Event"` | `AppendEventAsync` | Semantisches Business-Event (entity/entityId optional, gibt `eventId` zurück) |

**Die DSL-Schicht erzeugt ausschließlich `kind = "Change"`-Einträge** über `AppendChangeAsync`. Das ist bewusst:

- DSL-Rules beschreiben Zustandsänderungen an konkreten Entitäten → `entity` und `entityId` sind immer bekannt.
- Business Events ohne Entitätsbezug (z.B. `"SystemMaintenanceScheduled"`) werden weiterhin direkt über `ChangeWriter.AppendEventAsync` geschrieben.
- Die DSL ist kein Ersatz für den gesamten `ChangeWriter`, sondern eine ergonomische Schicht für den häufigsten Fall: _"Entity X hat sich geändert → schreibe ein typisiertes Change-Event"_.

Falls in Zukunft auch Business Events über die DSL erzeugt werden sollen, kann `DslChangeEvent` um ein optionales `Kind`-Property erweitert werden, das zwischen `AppendChangeAsync` und `AppendEventAsync` dispatcht.

---

## 17. Zusammenspiel mit der Projection-Seite

Die DSL-Schicht ist eine reine **Write-Side-Erweiterung**. Die Read-Side (Projektionen) wird vollständig vom existierenden Kernel abgedeckt:

```
Write Side (DSL)                          Read Side (Kernel)
─────────────────                         ──────────────────
ChangeRule<T>                             ProjectionWorker
    ↓                                         ↑
ChangeKernel.ProcessAsync                 ProjectionWorkerBase
    ↓                                         ↑
ChangeWriter.AppendChangeAsync            ChangeFeedReader
    ↓                                         ↑
papuma_event_feed ─────────────────────→ papuma_event_feed
```

### Versionierung: DSL → Projection Bridge

Die `DslChangeEvent.Version`-Property wird direkt als `version`-Spalte in der `papuma_event_feed` persistiert. Auf der Projection-Seite nutzt die `ProjectionRegistry` genau diesen Wert für versioniertes Dispatching:

```csharp
// Write Side: DSL-Rule erzeugt Event mit Version 1
kernel.Register(
    new ChangeRule<User>()
        .When(ctx => ctx.Operation == ChangeOperation.Update)
        .Emit(ctx => new DslChangeEvent
        {
            Stream = "users",
            Type = "UserUpdated",
            EntityId = ctx.Entity.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new UserUpdatedPayload(...)),
            Version = 1  // ← wird als version-Spalte persistiert
        }));

// Read Side: ProjectionRegistry dispatcht nach (EventType, Version)
registry.Register<UserUpdatedPayload>("UserUpdated", new UserUpdatedV1Handler());
```

Wenn sich das Payload-Schema ändert, wird eine neue Version eingeführt:
- Write Side: `Version = 2` in der Rule
- Read Side: `registry.Register<UserUpdatedPayloadV2>("UserUpdated", new UserUpdatedV2Handler())`
- Alte Events mit `Version = 1` werden weiterhin vom V1-Handler verarbeitet

### Existierende Kernel-Features (nicht dupliziert)

Die folgenden Features aus den vNEXT-Ideen (idea-3 bis idea-5) sind bereits im Kernel implementiert und werden von der DSL **nicht** neu gebaut:

| Feature | Kernel-Implementierung |
|---|---|
| Checkpointed Projection Pipeline | `ProjectionWorkerBase.SaveCheckpointAsync` + `LoadCheckpointAsync` |
| Idempotent Processing | TX pro Event mit atomarem Checkpoint-Update |
| Replay / Rebuild | `ResetProjectionStateAsync` + `IReplayableProjection` |
| Failure Handling mit Backoff | `RegisterFailureAsync` mit exponential Backoff + Dead-Letter |
| Multi-Projection Isolation | Jeder `ProjectionWorker` ist ein eigenständiger `BackgroundService` |
| Versioned Event Dispatching | `ProjectionRegistry` mit `(EventType, Version)`-Routing |
| Scope-Isolation | `ScopeFilter` in `ProjectionWorkerBase` |
| Visibility Guard | `xmin`/`pg_current_snapshot()` verhindert Lesen uncommitted Events |

---

## 18. Bewusst nicht in Scope

Die folgenden Konzepte aus den vNEXT-Diskussionen (insbesondere idea-3: Multi-Table Grouping) sind **bewusst nicht Teil dieser Spezifikation**:

### Multi-Table Change Grouping / Batch-Aggregation

**Problem:** Mehrere SQL-Änderungen in einer Transaktion (z.B. `UPDATE Orders` + `UPDATE Payments`) sollen als ein logisches Business-Event (`OrderPaid`) aggregiert werden.

**Warum nicht in Scope:**
- Die DSL arbeitet auf Entity-Ebene (`ChangeContext<T>` ist typisiert auf _eine_ Entität). Multi-Table Grouping erfordert ein übergeordnetes Batch-Konzept, das die DSL-Architektur signifikant verkomplizieren würde.
- Der existierende Kernel bietet bereits eine natürliche Gruppierung: Alle `AppendChangeAsync`-Aufrufe innerhalb einer `IUnitOfWork.ExecuteAsync`-Transaktion teilen sich dieselbe PostgreSQL-Transaction-ID. Ein zukünftiger Aggregation-Layer könnte auf dieser Basis arbeiten.
- Für den häufigsten Fall (1 Entity → 1 Event) ist die aktuelle DSL ausreichend.

**Möglicher Evolutionspfad:** Ein separater `ChangeAggregator`-Layer, der nach dem Commit mehrere Change-Events derselben Transaction-ID zu einem Domain-Event zusammenfasst. Dies wäre ein eigenständiges Feature und kein Teil der DSL.

### Projection Engine / Partitioning / Schema Evolution

Diese Themen sind bereits durch den existierenden Kernel abgedeckt (siehe Abschnitt 17) oder gehören in separate Design-Dokumente.
