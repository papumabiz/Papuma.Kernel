# Papuma.Kernel API Reference v0.1.0

This document is a companion to [Papuma.Kernel AI Framework Context](papuma-kernel-ai-framework-context.md). It provides the complete mechanical API surface — every type, method signature, parameter, default value, validation rule, DI registration, and code example. Designed to be copied into dependent application repositories so AI coding agents can develop against Papuma.Kernel without access to the source.

---

## Requirements

- **.NET 10** (`net10.0`)
- **Npgsql** (PostgreSQL ADO.NET driver)
- **PostgreSQL** with Row-Level Security support
- **NuGet packages**: `Papuma.Kernel`, `Papuma.Kernel.AspNetCore`
- **Required schema baseline**: version **5** (`papuma_schema_version` table)

---

## Quick Start

### Minimal DI Bootstrap

```csharp
using Papuma.Kernel;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNpgsqlDataSource(connectionString);

builder.Services.AddPapumaKernel(options =>
{
    options.MaxPayloadSizeBytes = 256 * 1024;
    options.UnitOfWork = new UnitOfWorkOptions { MaxRetries = 5 };
});
```

### Write a Change Event in a Transaction

```csharp
public class CreateOrderHandler
{
    private readonly IUnitOfWork _uow;
    private readonly ChangeWriter _changeWriter;

    public CreateOrderHandler(IUnitOfWork uow, ChangeWriter changeWriter)
    {
        _uow = uow;
        _changeWriter = changeWriter;
    }

    public async Task HandleAsync(CreateOrderCommand cmd, ScopeContext scope, CancellationToken ct)
    {
        await _uow.ExecuteAsync(async (conn, tx, ct) =>
        {
            // 1. Write domain state (your own SQL)
            // INSERT INTO orders ...

            // 2. Append change feed record
            await _changeWriter.AppendChangeAsync(
                tx, scope,
                entity: "Order",
                entityId: cmd.OrderId,
                eventType: "OrderCreated",
                version: 1,
                payloadJson: """{"orderId":"...","amount":42.00}""",
                actorId: cmd.ActorId,
                correlationId: cmd.CorrelationId,
                idempotencyKey: cmd.IdempotencyKey,
                ct: ct);
        }, ct);
    }
}
```

### Register Workers and ASP.NET Scope

```csharp
// Projection handler (in-DB read model)
builder.Services.AddProjection<MyProjection>(options =>
{
    options.BatchSize = 100;
    options.PollInterval = TimeSpan.FromMilliseconds(100);
});

// External projection handler (out-of-process target)
builder.Services.AddExternalProjection<MyExternalProjection>();

// Outbox publisher (message broker, webhook, etc.)
builder.Services.AddOutboxWorker<MyOutboxPublisher>();

// Replay service (for full projection rebuilds)
builder.Services.AddReplayService();

// Retrieval worker (GDPR cleanup)
builder.Services.AddRetentionWorker();

// Sensitive data store (PII indirection)
builder.Services.AddSensitiveDataStore();

// ASP.NET Core scope resolution
builder.Services.AddPapumaScope<MyScopeResolver>();

var app = builder.Build();
app.UseScopeResolution();
app.MapHealthChecks("/health");
```

---

## Configuration Model

All configuration uses the **Options Pattern** — each component has its own `*Options` class with sensible defaults. The top-level `PapumaKernelOptions` distributes shared settings.

| Aspect | Option Class | Default |
|--------|-------------|---------|
| Top-level kernel | `PapumaKernelOptions` | — |
| Change writer | `ChangeWriterOptions` | `MaxPayloadSizeBytes = 256 * 1024` |
| Change writer (events) | `ChangeWriterOptions` | `MaxPayloadSizeBytes = 256 * 1024` |
| Outbox writer | `OutboxWriterOptions` | `MaxPayloadSizeBytes = 256 * 1024` |
| Outbox worker | `OutboxWorkerOptions` | Poll 100ms, Batch 100, 10 attempts, 5s-5m backoff |
| Projection worker | `ProjectionWorkerOptions` | Poll 100ms, Batch 100, 10 attempts, 5s-5m backoff |
| Retention worker | `RetentionWorkerOptions` | Poll 1h, 365d window, Batch 1000 |
| Unit of work | `UnitOfWorkOptions` | 3 retries, 100ms base delay |
| Health check | `ProjectionHealthCheckOptions` | `MaxAllowedLag = 1000` |

### Service Lifetimes

All core services are registered as **Singleton** unless otherwise noted. ASP.NET Core scope services (`IScopeResolver`) are **Scoped**.

---

# Namespace-by-Namespace Reference

---

## Papuma.Kernel (root)

### PapumaKernelOptions

**Kind**: sealed class
**Namespace**: `Papuma.Kernel`
**Lifetime**: (options object, not directly registered)

Top-level configuration for all Papuma.Kernel services registered via `AddPapumaKernel`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxPayloadSizeBytes` | `int` | `256 * 1024` (256 KB) | Maximum payload size in bytes shared by all writers. |
| `UnitOfWork` | `UnitOfWorkOptions?` | `null` | Unit of work retry configuration. `null` uses built-in defaults. |

---

### ServiceCollectionExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel`

Convenience extensions for registering all Papuma.Kernel services at once.

#### Methods

```csharp
public static IServiceCollection AddPapumaKernel(
    this IServiceCollection services,
    Action<PapumaKernelOptions>? configure = null)
```

Registers the core Papuma.Kernel services as singletons:
- `ChangeWriter`
- `ChangeFeedReader`
- `ChangeWriter`
- `OutboxWriter`
- `GdprProcessor`
- `SchemaVersionChecker`
- `ISensitiveDataStore` (via `NpgsqlSensitiveDataStore`)
- `ISensitiveDataResolver` (via `NpgsqlSensitiveDataStore`)
- `IUnitOfWork` (via `NpgsqlUnitOfWork`)

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `services` | `IServiceCollection` | Yes | The service collection. |
| `configure` | `Action<PapumaKernelOptions>?` | No | Optional callback to configure kernel options. |

**Returns**: The original service collection for chaining.

---

## Papuma.Kernel.ChangeFeed

### ChangeRecord

**Kind**: sealed record
**Namespace**: `Papuma.Kernel.ChangeFeed`

Represents a persisted change feed entry.

```csharp
public sealed record ChangeRecord(
    long SequenceId,
    string Kind,
    Guid? EventId,
    string? Entity,
    string? EntityId,
    string EventType,
    int? Version,
    string? CorrelationId,
    string? CausationId,
    string ActorId,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    ScopeType Scope,
    string? TenantId
);
```

| Property | Type | Description |
|----------|------|-------------|
| `SequenceId` | `long` | The sequence identifier assigned by the store. |
| `Kind` | `string` | The discriminator: `"Change"` (state mutation) or `"Event"` (business signal). |
| `EventId` | `Guid?` | The UUID-based event identity; `null` for Change records (no Outbox correlation). |
| `Entity` | `string?` | The logical entity name, or `null` for entity-independent events. |
| `EntityId` | `string?` | The entity identifier, or `null` when no entity is bound. |
| `EventType` | `string` | The event type that describes the change or signal. |
| `Version` | `int?` | The aggregate version for Change records; `null` for Event records. |
| `CorrelationId` | `string?` | Optional correlation identifier. |
| `CausationId` | `string?` | Optional causation identifier. |
| `ActorId` | `string` | The actor that caused the change. |
| `PayloadJson` | `string` | The JSON payload associated with the change. |
| `OccurredAt` | `DateTimeOffset` | The timestamp at which the record was persisted. |
| `Scope` | `ScopeType` | The scope associated with the change (`Platform` or `Tenant`). |
| `TenantId` | `string?` | The tenant identifier for tenant scope; otherwise `null`. |

---

### ChangeWriterOptions

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.ChangeFeed`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxPayloadSizeBytes` | `int` | `256 * 1024` | Maximum allowed payload size in bytes. |

---

### ChangeWriter

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.ChangeFeed`
**Lifetime**: Singleton (registered by `AddPapumaKernel`)

Writes validated change feed records into the PostgreSQL-backed change feed store. Handles idempotency key conflicts gracefully (returns without error on duplicate key).

#### Constructor

```csharp
public ChangeWriter(ChangeWriterOptions? options = null)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `options` | `ChangeWriterOptions?` | No | Optional writer configuration. Defaults are used when `null`. |

#### Methods

```csharp
public async Task AppendChangeAsync(
    NpgsqlTransaction transaction,
    ScopeContext scope,
    string entity,
    string entityId,
    string eventType,
    int version,
    string payloadJson,
    string actorId,
    string? correlationId = null,
    string? causationId = null,
    string? idempotencyKey = null,
    CancellationToken ct = default)
```

Appends a new scoped change feed record to the current transaction. Sets `app.current_scope` and `app.current_tenant` session variables via `SET LOCAL` before the INSERT.

| Parameter | Type | Required | Validation |
|-----------|------|----------|------------|
| `transaction` | `NpgsqlTransaction` | Yes | Must not be null. |
| `scope` | `ScopeContext` | Yes | Must not be null. |
| `entity` | `string` | Yes | Pattern: `[A-Za-z][A-Za-z0-9_]{1,100}` |
| `entityId` | `string` | Yes | Non-empty, max 200 chars. |
| `eventType` | `string` | Yes | Pattern: `[A-Za-z][A-Za-z0-9_]{2,100}` |
| `version` | `int` | Yes | Must be >= 1. |
| `payloadJson` | `string` | Yes | UTF-8 byte count must be <= `MaxPayloadSizeBytes`. |
| `actorId` | `string` | Yes | Non-empty, max 200 chars. |
| `correlationId` | `string?` | No | — |
| `causationId` | `string?` | No | — |
| `idempotencyKey` | `string?` | No | If provided: non-empty, max 200 chars. |
| `ct` | `CancellationToken` | No | — |

**Idempotency behavior**: When an `idempotencyKey` is provided and a row with the same (scope, tenant, key) already exists, the method silently returns — no exception is thrown. This relies on the unique constraint `ux_papuma_event_feed_idempotency_key` on the `papuma_event_feed` table.

**Database table**: `papuma_event_feed`

```csharp
public void ValidateInputs(
    string entity,
    string entityId,
    string eventType,
    int version,
    string payloadJson,
    string actorId,
    string? idempotencyKey = null)
```

Validates change feed input values. Throws `ArgumentException` on any validation failure. Called by `AppendChangeAsync` before writing.

---

### ChangeFeedReader

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.ChangeFeed`
**Lifetime**: Singleton (registered by `AddPapumaKernel` or `AddChangeFeedReader`)

Reads change feed records for ad-hoc queries (debugging, exports, admin tools).

#### Constructor

```csharp
public ChangeFeedReader(NpgsqlDataSource dataSource)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `dataSource` | `NpgsqlDataSource` | Yes | The data source used to read change feed records. |

#### Methods

```csharp
public async Task<IReadOnlyList<ChangeRecord>> GetByEntityAsync(
    ScopeContext scope,
    string entity,
    string entityId,
    int limit = 1000,
    CancellationToken ct = default)
```

Gets all change feed records for a specific entity, ordered by `sequence_id`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scope` | `ScopeContext` | Yes | The scope context to query in. Must not be null. |
| `entity` | `string` | Yes | The entity name. |
| `entityId` | `string` | Yes | The entity identifier. |
| `limit` | `int` | No | Maximum number of records. Must be >= 1. Default: 1000. |
| `ct` | `CancellationToken` | No | — |

```csharp
public async Task<IReadOnlyList<ChangeRecord>> GetBySequenceRangeAsync(
    ScopeContext scope,
    long fromSequenceId,
    long toSequenceId,
    int limit = 1000,
    CancellationToken ct = default)
```

Gets change feed records for a sequence id range (inclusive on both ends), ordered by `sequence_id`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scope` | `ScopeContext` | Yes | The scope context to query in. |
| `fromSequenceId` | `long` | Yes | Inclusive start sequence id. Must be >= 1. |
| `toSequenceId` | `long` | Yes | Inclusive end sequence id. Must be >= `fromSequenceId`. |
| `limit` | `int` | No | Maximum number of records. Must be >= 1. Default: 1000. |
| `ct` | `CancellationToken` | No | — |

```csharp
public async Task<long> GetLatestSequenceIdAsync(
    ScopeContext scope,
    CancellationToken ct = default)
```

Gets the latest sequence id visible in the selected scope. Returns `0` if no records exist.

---

### ChangeFeedExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.ChangeFeed`

```csharp
public static IServiceCollection AddChangeFeedReader(this IServiceCollection services)
```

Registers `ChangeFeedReader` as a singleton service. (Already included in `AddPapumaKernel`; use standalone only when needed without the full registration.)

---

## Papuma.Kernel.Events

### AppendEventAsync (on ChangeWriter)

The `ChangeWriter` (documented in the `Papuma.Kernel.ChangeFeed` namespace above) also exposes a method for business events:

#### Method

```csharp
public async Task AppendEventAsync(
    NpgsqlTransaction transaction,
    ScopeContext scope,
    string eventType,
    string actorId,
    string payloadJson,
    string? entity = null,
    string? entityId = null,
    string? correlationId = null,
    string? causationId = null,
    string? idempotencyKey = null,
    CancellationToken ct = default)
```

Appends a business event (`kind='Event'`) to the current transaction. Returns the generated `Guid` event identifier (or the existing one on idempotency conflict).

| Parameter | Type | Required | Validation |
|-----------|------|----------|------------|
| `transaction` | `NpgsqlTransaction` | Yes | Must not be null. |
| `scope` | `ScopeContext` | Yes | Must not be null. |
| `eventType` | `string` | Yes | Pattern: `[A-Za-z][A-Za-z0-9_]{2,100}` |
| `actorId` | `string` | Yes | Non-empty, max 200 chars. |
| `payloadJson` | `string` | Yes | UTF-8 byte count <= `MaxPayloadSizeBytes`. |
| `entity` | `string?` | No | If provided: `[A-Za-z][A-Za-z0-9_]{1,100}` |
| `entityId` | `string?` | No | If provided: max 200 chars. |
| `correlationId` | `string?` | No | — |
| `causationId` | `string?` | No | — |
| `idempotencyKey` | `string?` | No | If provided: non-empty, max 200 chars. |
| `ct` | `CancellationToken` | No | — |

**Idempotency behavior**: On unique violation of `ux_papuma_event_feed_idempotency_key`, the existing `event_id` is looked up and returned. No exception is thrown.

**Database table**: `papuma_event_feed`

---

### OutboxWriterOptions

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Events`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxPayloadSizeBytes` | `int` | `256 * 1024` | Maximum allowed payload size in bytes. |

---

### OutboxWriter

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Events`
**Lifetime**: Singleton (registered by `AddPapumaKernel`)

Enqueues external publication work into the transactional outbox.

#### Constructor

```csharp
public OutboxWriter(OutboxWriterOptions? options = null)
```

#### Methods

```csharp
public async Task EnqueueAsync(
    NpgsqlTransaction transaction,
    ScopeContext scope,
    Guid eventId,
    string eventType,
    string payloadJson,
    CancellationToken ct = default)
```

Enqueues a scoped outbox message in the current transaction.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `transaction` | `NpgsqlTransaction` | Yes | The ambient PostgreSQL transaction. |
| `scope` | `ScopeContext` | Yes | The scope context for the outbox message. |
| `eventId` | `Guid` | Yes | The event identifier (typically from `ChangeWriter.AppendEventAsync`). |
| `eventType` | `string` | Yes | The business event type. |
| `payloadJson` | `string` | Yes | UTF-8 byte count <= `MaxPayloadSizeBytes`. |
| `ct` | `CancellationToken` | No | — |

**Database table**: `papuma_event_outbox`

---

### IOutboxPublisher

**Kind**: interface
**Namespace**: `Papuma.Kernel.Events`

Publishes pending outbox messages to an external system (message broker, webhook, etc.). **Implementations must be idempotent** — the `OutboxWorker` delivers with at-least-once semantics.

#### Methods

```csharp
Task<bool> PublishAsync(
    ScopeContext scope,
    Guid eventId,
    string eventType,
    string payloadJson,
    CancellationToken ct = default)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `scope` | `ScopeContext` | The scope context for the message. |
| `eventId` | `Guid` | The unique event identifier (for deduplication). |
| `eventType` | `string` | The business event type. |
| `payloadJson` | `string` | The JSON payload to publish. |
| `ct` | `CancellationToken` | — |

**Returns**: `true` if the message was published successfully; `false` if it should be retried.

---

### OutboxWorkerOptions

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Events`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PollInterval` | `TimeSpan` | `100 ms` | Idle wait time between polling cycles. |
| `BatchSize` | `int` | `100` | Max outbox records loaded per batch. |
| `MaxAttemptsPerMessage` | `int` | `10` | Max failed attempts before dead-lettering. |
| `BaseRetryDelay` | `TimeSpan` | `5 sec` | Base delay for exponential retry backoff. |
| `MaxRetryDelay` | `TimeSpan` | `5 min` | Maximum delay cap for exponential retry backoff. |

---

### OutboxWorker

**Kind**: sealed class (extends `BackgroundService`)
**Namespace**: `Papuma.Kernel.Events`

Polls the transactional outbox and publishes pending messages with at-least-once semantics. Registered automatically by `AddOutboxWorker<TPublisher>()`.

#### Constructor

```csharp
public OutboxWorker(
    IOutboxPublisher publisher,
    NpgsqlDataSource dataSource,
    ILogger<OutboxWorker> logger,
    OutboxWorkerOptions? options = null,
    ScopeFilter? scopeFilter = null)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `publisher` | `IOutboxPublisher` | Yes | The user's publisher implementation. |
| `dataSource` | `NpgsqlDataSource` | Yes | Database data source. |
| `logger` | `ILogger<OutboxWorker>` | Yes | Logger. |
| `options` | `OutboxWorkerOptions?` | No | Polling/retry configuration. |
| `scopeFilter` | `ScopeFilter?` | No | Restrict processing to a specific scope. `null` = all scopes. |

**Database tables**: `papuma_event_outbox` (read), projection-style checkpoint tracking.

---

### OutboxExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.Events`

```csharp
public static IServiceCollection AddOutboxWorker<TPublisher>(
    this IServiceCollection services,
    Action<OutboxWorkerOptions>? configure = null,
    ScopeFilter? scopeFilter = null)
    where TPublisher : class, IOutboxPublisher
```

Registers an outbox publisher (`TPublisher`) as a singleton and adds `OutboxWorker` as a hosted service.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `services` | `IServiceCollection` | Yes | The service collection. |
| `configure` | `Action<OutboxWorkerOptions>?` | No | Optional worker configuration callback. |
| `scopeFilter` | `ScopeFilter?` | No | Optional scope filter for the worker. |

---

## Papuma.Kernel.Transactions

### UnitOfWorkOptions

**Kind**: class
**Namespace**: `Papuma.Kernel.Transactions`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxRetries` | `int` | `3` | Maximum number of attempts for transient database failures. Must be >= 1. |
| `BaseRetryDelay` | `TimeSpan` | `100 ms` | Base delay between retry attempts. Jitter (0-50ms) is added automatically. Must not be negative. |

---

### IUnitOfWork

**Kind**: interface
**Namespace**: `Papuma.Kernel.Transactions`
**Lifetime**: Singleton (registered by `AddPapumaKernel` as `NpgsqlUnitOfWork`)

Defines a transactional execution boundary for database operations.

#### Methods

```csharp
Task ExecuteAsync(
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action,
    CancellationToken ct = default)
```

Executes the specified action in a single database transaction. Retries on transient PostgreSQL errors (see below).

| Parameter | Type | Description |
|-----------|------|-------------|
| `action` | `Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>` | Action to execute with connection and transaction context. Must not be null. |
| `ct` | `CancellationToken` | — |

**Transient SQL states that trigger retry**:
- `40001` — serialization_failure
- `40P01` — deadlock_detected
- `08006` — connection_failure
- `08001` — sqlclient_unable_to_establish_sqlconnection
- `57P03` — cannot_connect_now

**Retry strategy**: Exponential backoff with jitter. On attempt N, delay = `BaseRetryDelay * 2^(N-1) + Random(0, 50ms)`. Non-transient exceptions are re-thrown immediately after rollback.

---

### NpgsqlUnitOfWork

**Kind**: sealed class (implements `IUnitOfWork`)
**Namespace**: `Papuma.Kernel.Transactions`

PostgreSQL-backed implementation of `IUnitOfWork`.

#### Constructor

```csharp
public NpgsqlUnitOfWork(
    NpgsqlDataSource dataSource,
    UnitOfWorkOptions? options = null,
    ILogger<NpgsqlUnitOfWork>? logger = null)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `dataSource` | `NpgsqlDataSource` | Yes | Data source used to open connections. |
| `options` | `UnitOfWorkOptions?` | No | Retry options. Defaults when `null`. |
| `logger` | `ILogger<NpgsqlUnitOfWork>?` | No | Optional logger for retry diagnostics. |

**Throws**: `ArgumentOutOfRangeException` if `MaxRetries < 1` or `BaseRetryDelay` is negative.

---

## Papuma.Kernel.Tenancy

### ScopeType

**Kind**: enum
**Namespace**: `Papuma.Kernel.Tenancy`

| Value | Integer | Description |
|-------|---------|-------------|
| `Platform` | `0` | Platform-wide data not bound to a tenant. |
| `Tenant` | `1` | Data bound to a concrete tenant. |

---

### ScopeContext

**Kind**: sealed record
**Namespace**: `Papuma.Kernel.Tenancy`

Represents the current data scope for platform- or tenant-scoped operations.

| Property | Type | Description |
|----------|------|-------------|
| `Scope` | `ScopeType` | The resolved scope kind. |
| `TenantId` | `string?` | The tenant identifier for `Tenant` scope; otherwise `null`. |

**No public constructor** — use factory methods:

```csharp
ScopeContext Platform()                              // Platform scope, no tenant
ScopeContext Tenant(string tenantId)               // Tenant scope with validated tenant ID
```

**Tenant ID validation**: Pattern `[A-Za-z][A-Za-z0-9_]{1,100}`. `ArgumentException` on invalid input.

---

### ScopeFilter

**Kind**: sealed record
**Namespace**: `Papuma.Kernel.Tenancy`

Defines a scope filter for background workers that read across scopes. Unlike `ScopeContext` (always a concrete scope), `ScopeFilter` can also represent "all scopes".

| Property | Type | Description |
|----------|------|-------------|
| `Scope` | `ScopeContext?` | The underlying scope context, or `null` when the filter matches all scopes. |
| `IsAll` | `bool` | `true` when this filter matches all scopes (no restriction). |

#### Factory Methods

```csharp
ScopeFilter All()                              // Matches all scopes (no restriction)
ScopeFilter Platform()                         // Platform scope only
ScopeFilter Tenant(string tenantId)            // Specific tenant scope only
ScopeFilter FromScope(ScopeContext scope)      // From an existing ScopeContext
```

---

### ScopeConnectionExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.Tenancy`

Extension methods for setting scope context variables on a PostgreSQL connection.

```csharp
public static async Task SetScopeAsync(
    this NpgsqlConnection connection,
    ScopeContext scope,
    CancellationToken ct = default)
```

Sets `app.current_scope` and `app.current_tenant` as `SET LOCAL` session variables on the connection. `SET LOCAL` scopes the values to the current transaction; callers must ensure a transaction is active.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `connection` | `NpgsqlConnection` | Yes | The open PostgreSQL connection. |
| `scope` | `ScopeContext` | Yes | The scope context to apply. |
| `ct` | `CancellationToken` | No | — |

**PostgreSQL session variables set**:
- `app.current_scope` = `"Platform"` or `"Tenant"`
- `app.current_tenant` = tenant ID string (empty string for Platform scope)

These variables drive Row-Level Security (RLS) policies on framework tables.

---

### IScopeDataSourceFactory

**Kind**: interface
**Namespace**: `Papuma.Kernel.Tenancy`

Creates a tenant-specific `NpgsqlDataSource` for database-per-tenant architectures.

#### Methods

```csharp
NpgsqlDataSource Create(ScopeContext scope)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `scope` | `ScopeContext` | The scope context identifying the tenant. |

---

### ScopeDataSourceFactory

**Kind**: class (implements `IScopeDataSourceFactory`)
**Namespace**: `Papuma.Kernel.Tenancy`

Default implementation of `IScopeDataSourceFactory`.

---

## Papuma.Kernel.Projections

### IProjectionHandler

**Kind**: interface
**Namespace**: `Papuma.Kernel.Projections`

Handles change feed records and projects them into a read model **within the same transaction** as the checkpoint update. Use this for in-database read models.

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Unique projection name used for checkpoints and failure tracking. |
| `EventTypes` | `IReadOnlyCollection<string>` | The event types consumed by this projection. |

#### Methods

```csharp
Task HandleAsync(
    ChangeRecord record,
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    CancellationToken ct = default)
```

Applies the supplied change record within the provided transaction. The worker manages the transaction boundary — the projection's writes and the checkpoint update are atomic.

| Parameter | Type | Description |
|-----------|------|-------------|
| `record` | `ChangeRecord` | The change record to handle. |
| `connection` | `NpgsqlConnection` | The database connection used by the worker. |
| `transaction` | `NpgsqlTransaction` | The transaction that scopes handler writes and checkpoint updates. |
| `ct` | `CancellationToken` | — |

**Database tables**: User's read model tables + `papuma_projection_checkpoint` (framework-managed).

---

### IExternalProjectionHandler

**Kind**: interface
**Namespace**: `Papuma.Kernel.Projections`

Handles change feed records for **external systems** where transactional coupling is not possible (e.g., search index, cache, third-party API). Checkpoint is updated after successful external write.

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Unique projection name used for checkpoints and failure tracking. |
| `EventTypes` | `IReadOnlyCollection<string>` | The event types consumed by this projection. |

#### Methods

```csharp
Task HandleAsync(
    ChangeRecord record,
    CancellationToken ct = default)
```

Applies the supplied change record to an external target.

| Parameter | Type | Description |
|-----------|------|-------------|
| `record` | `ChangeRecord` | The change record to handle. |
| `ct` | `CancellationToken` | — |

---

### IProjectionLagProvider

**Kind**: interface
**Namespace**: `Papuma.Kernel.Projections`

Provides lag snapshots for a projection worker.

| Property | Type | Description |
|----------|------|-------------|
| `ProjectionName` | `string` | Unique projection name. |

#### Methods

```csharp
Task<ProjectionLagSnapshot> GetLagSnapshotAsync(CancellationToken ct = default)
```

Gets a point-in-time lag snapshot for the projection.

---

### IReplayableProjection

**Kind**: interface
**Namespace**: `Papuma.Kernel.Projections`

Allows a projection to reset its read model before a replay starts.

#### Methods

```csharp
Task PrepareReplayAsync(CancellationToken ct = default)
```

Prepares the projection for a full replay (typically truncating or resetting the read model).

---

### IVersionedHandler\<TPayload\>

**Kind**: interface
**Namespace**: `Papuma.Kernel.Projections`

Handles a specific version of a versioned event payload. Use with `ProjectionRegistry` for type-safe payload deserialization within a projection handler.

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Version` | `int` | The version handled by this implementation. |

#### Methods

```csharp
Task HandleAsync(
    TPayload data,
    ChangeRecord record,
    CancellationToken ct = default)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `data` | `TPayload` | The deserialized payload. |
| `record` | `ChangeRecord` | The raw change record. |
| `ct` | `CancellationToken` | — |

---

### ProjectionWorkerOptions

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Projections`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PollInterval` | `TimeSpan` | `100 ms` | Idle wait time between polling cycles. |
| `BatchSize` | `int` | `100` | Max change records loaded per batch. |
| `MaxAttemptsPerEvent` | `int` | `10` | Max failed attempts before an event is dead-lettered. |
| `BaseRetryDelay` | `TimeSpan` | `5 sec` | Base delay for exponential retry backoff. |
| `MaxRetryDelay` | `TimeSpan` | `5 min` | Maximum delay cap for exponential retry backoff. |

---

### ProjectionWorker

**Kind**: sealed class (extends `BackgroundService`, implements `IProjectionLagProvider`)
**Namespace**: `Papuma.Kernel.Projections`

Polls the change feed and applies matching events to an **in-DB projection** with at-least-once semantics.

#### Constructor

```csharp
public ProjectionWorker(
    IProjectionHandler handler,
    NpgsqlDataSource dataSource,
    ILogger<ProjectionWorker> logger,
    ProjectionWorkerOptions? options = null,
    ScopeFilter? scopeFilter = null)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `handler` | `IProjectionHandler` | Yes | The projection handler. |
| `dataSource` | `NpgsqlDataSource` | Yes | Database data source. |
| `logger` | `ILogger<ProjectionWorker>` | Yes | Logger. |
| `options` | `ProjectionWorkerOptions?` | No | Polling/retry configuration. |
| `scopeFilter` | `ScopeFilter?` | No | Restrict processing to a specific scope. |

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `ProjectionName` | `string` | Unique projection name (from handler). |

#### Methods

```csharp
Task RequestReplayAsync(CancellationToken ct = default)    // Request a full replay
Task<ProjectionLagSnapshot> GetLagSnapshotAsync(CancellationToken ct = default)  // Current lag
```

**Database tables**: `papuma_event_feed` (read), `papuma_projection_checkpoint` (track position), `papuma_projection_failures` (dead-letter).

---

### ExternalProjectionWorker

**Kind**: sealed class (extends `BackgroundService`, implements `IProjectionLagProvider`)
**Namespace**: `Papuma.Kernel.Projections`

Polls the change feed and applies matching events to an **external projection** with at-least-once semantics.

#### Constructor

```csharp
public ExternalProjectionWorker(
    IExternalProjectionHandler handler,
    NpgsqlDataSource dataSource,
    ILogger<ExternalProjectionWorker> logger,
    ProjectionWorkerOptions? options = null,
    ScopeFilter? scopeFilter = null)
```

Same structure as `ProjectionWorker` but with `IExternalProjectionHandler`.

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `ProjectionName` | `string` | Unique projection name (from handler). |

#### Methods

```csharp
Task<ProjectionLagSnapshot> GetLagSnapshotAsync(CancellationToken ct = default)
```

---

### ProjectionRegistry

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Projections`

Routes versioned event payloads to registered `IVersionedHandler<TPayload>` handlers. Provides type-safe JSON deserialization within a projection handler.

#### Constructor

```csharp
public ProjectionRegistry()  // parameterless
```

#### Methods

```csharp
public void Register<TPayload>(
    string eventType,
    IVersionedHandler<TPayload> handler)
```

Registers a versioned handler for an event type. Keyed by `(eventType, handler.Version)`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `eventType` | `string` | Yes | The event type handled by the projection. |
| `handler` | `IVersionedHandler<TPayload>` | Yes | The version-specific handler. |

```csharp
public async Task DispatchAsync(
    ChangeRecord record,
    CancellationToken ct = default)
```

Dispatches a change record to its matching versioned handler. Deserializes `record.PayloadJson` into `TPayload` using `System.Text.Json`. Throws `InvalidOperationException` if no handler is registered for `(record.EventType, record.Version)`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `record` | `ChangeRecord` | Yes | The record to dispatch. Must not be null. |
| `ct` | `CancellationToken` | No | — |

---

### ProjectionLagSnapshot

**Kind**: sealed record
**Namespace**: `Papuma.Kernel.Projections`

```csharp
public sealed record ProjectionLagSnapshot(
    string ProjectionName,
    long Checkpoint,
    long LatestSequenceId,
    long Lag
);
```

| Property | Type | Description |
|----------|------|-------------|
| `ProjectionName` | `string` | The unique projection name. |
| `Checkpoint` | `long` | The current checkpoint sequence id. |
| `LatestSequenceId` | `long` | The latest relevant change feed sequence id. |
| `Lag` | `long` | Non-negative lag value (`LatestSequenceId - Checkpoint`). |

---

### ReplayService

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Projections`

Coordinates replay requests for registered projection workers. Auto-discovers all `ProjectionWorker` instances.

#### Constructor

```csharp
public ReplayService(IReadOnlyDictionary<string, ProjectionWorker> workers)
```

Only `IReplayableProjection` projections are supported. (Note: external projections currently not replayable through this service.)

#### Methods

```csharp
public Task RequestReplayAsync(
    string projectionName,
    CancellationToken ct = default)
```

Requests a replay for the specified projection. If the projection implements `IReplayableProjection`, `PrepareReplayAsync` is called first, then the checkpoint is reset to `0`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `projectionName` | `string` | Yes | The projection to replay. |
| `ct` | `CancellationToken` | No | — |

---

### ProjectionExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.Projections`

```csharp
public static IServiceCollection AddProjection<THandler>(
    this IServiceCollection services,
    Action<ProjectionWorkerOptions>? configure = null,
    ScopeFilter? scopeFilter = null)
    where THandler : class, IProjectionHandler
```

Registers a projection handler as singleton and adds `ProjectionWorker` as a hosted service.

```csharp
public static IServiceCollection AddExternalProjection<THandler>(
    this IServiceCollection services,
    Action<ProjectionWorkerOptions>? configure = null,
    ScopeFilter? scopeFilter = null)
    where THandler : class, IExternalProjectionHandler
```

Registers an external projection handler as singleton and adds `ExternalProjectionWorker` as a hosted service.

```csharp
public static IServiceCollection AddReplayService(this IServiceCollection services)
```

Registers a `ReplayService` that auto-discovers all `ProjectionWorker` instances registered as `IHostedService`.

---

## Papuma.Kernel.Gdpr

### SensitiveRef

**Kind**: readonly record struct
**Namespace**: `Papuma.Kernel.Gdpr`

Represents a stable reference to versioned sensitive data. Used as an indirection to keep sensitive payloads out of event streams — instead of embedding PII directly in `papuma_event_feed` or `papuma_event_feed` payloads, store only a `SensitiveRef` and resolve via `ISensitiveDataResolver`.

```csharp
public readonly record struct SensitiveRef(Guid Value)
```

| Property | Type | Description |
|----------|------|-------------|
| `Value` | `Guid` | The underlying GUID. |
| `IsEmpty` | `bool` | `true` when `Value == Guid.Empty`. |

#### Factory Method

```csharp
public static SensitiveRef New() => new(Guid.NewGuid());  // Creates a new random reference
```

---

### SensitiveDataState

**Kind**: enum
**Namespace**: `Papuma.Kernel.Gdpr`

| Value | Integer | Description |
|-------|---------|-------------|
| `Active` | `0` | Payload is available for resolution. |
| `Redacted` | `1` | Payload was redacted and should no longer be exposed. |
| `Deleted` | `2` | Payload was deleted and should be treated as unavailable. |

---

### SensitiveDataVersion

**Kind**: sealed record
**Namespace**: `Papuma.Kernel.Gdpr`

```csharp
public sealed record SensitiveDataVersion(
    SensitiveRef SensitiveRef,
    int Version,
    int SchemaVersion,
    string PayloadJson,
    SensitiveDataState State,
    bool LegalHold,
    string ActorId,
    DateTimeOffset CreatedAt,
    string? Reason,
    ScopeContext Scope
);
```

| Property | Type | Description |
|----------|------|-------------|
| `SensitiveRef` | `SensitiveRef` | The stable reference. |
| `Version` | `int` | Monotonically increasing version number. |
| `SchemaVersion` | `int` | Application schema version for the payload shape. |
| `PayloadJson` | `string` | The JSON payload (empty for redacted/deleted states). |
| `State` | `SensitiveDataState` | Lifecycle state: `Active`, `Redacted`, or `Deleted`. |
| `LegalHold` | `bool` | Whether legal hold is enforced. |
| `ActorId` | `string` | The actor that created this version. |
| `CreatedAt` | `DateTimeOffset` | Creation timestamp. |
| `Reason` | `string?` | Optional documented reason for the lifecycle change. |
| `Scope` | `ScopeContext` | The scope context. |

**Database table**: `papuma_sensitive_data_versions`

---

### ISensitiveDataStore

**Kind**: interface
**Namespace**: `Papuma.Kernel.Gdpr`
**Lifetime**: Singleton (registered by `AddSensitiveDataStore` as `NpgsqlSensitiveDataStore`)

Provides versioned write and lifecycle operations for sensitive payloads. All operations are append-only — each call creates a new version row.

#### Methods

```csharp
Task<SensitiveDataVersion>.AppendChangeAsync(
    ScopeContext scope,
    SensitiveRef sensitiveRef,
    int schemaVersion,
    string payloadJson,
    string actorId,
    string? reason = null,
    CancellationToken ct = default)
```

Appends a new active sensitive payload version. Returns the created `SensitiveDataVersion`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scope` | `ScopeContext` | Yes | The scope context. |
| `sensitiveRef` | `SensitiveRef` | Yes | The stable reference. |
| `schemaVersion` | `int` | Yes | Application schema version for the payload shape. |
| `payloadJson` | `string` | Yes | The sensitive JSON payload. |
| `actorId` | `string` | Yes | The actor writing the data. |
| `reason` | `string?` | No | Optional documented reason. |
| `ct` | `CancellationToken` | No | — |

```csharp
Task<SensitiveDataVersion?> GetLatestAsync(
    ScopeContext scope,
    SensitiveRef sensitiveRef,
    CancellationToken ct = default)
```

Returns the latest stored version, or `null` when no versions exist.

```csharp
Task MarkRedactedAsync(
    ScopeContext scope,
    SensitiveRef sensitiveRef,
    string actorId,
    string reason,
    CancellationToken ct = default)
```

Appends a version with `State = Redacted`. The payload is overwritten with `"{}"`. After this call, `ISensitiveDataResolver.TryResolveLatestPayloadAsync` returns `null`.

```csharp
Task MarkDeletedAsync(
    ScopeContext scope,
    SensitiveRef sensitiveRef,
    string actorId,
    string reason,
    CancellationToken ct = default)
```

Appends a version with `State = Deleted`. After this call, resolution returns `null`.

```csharp
Task SetLegalHoldAsync(
    ScopeContext scope,
    SensitiveRef sensitiveRef,
    bool enabled,
    string actorId,
    string reason,
    CancellationToken ct = default)
```

Appends a new version with updated legal hold state. Does not change the payload or state — only toggles `LegalHold`.

---

### ISensitiveDataResolver

**Kind**: interface
**Namespace**: `Papuma.Kernel.Gdpr`
**Lifetime**: Singleton (registered by `AddSensitiveDataStore` as `NpgsqlSensitiveDataStore`)

Resolves sensitive payloads for read-side processing.

#### Methods

```csharp
Task<string?> TryResolveLatestPayloadAsync(
    ScopeContext scope,
    SensitiveRef sensitiveRef,
    CancellationToken ct = default)
```

Resolves the latest payload for a sensitive reference when it is still active. Returns `null` when the reference is missing, redacted, or deleted.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scope` | `ScopeContext` | Yes | The scope context. |
| `sensitiveRef` | `SensitiveRef` | Yes | The reference to resolve. |
| `ct` | `CancellationToken` | No | — |

---

### NpgsqlSensitiveDataStore

**Kind**: sealed class (implements `ISensitiveDataStore`, `ISensitiveDataResolver`)
**Namespace**: `Papuma.Kernel.Gdpr`

PostgreSQL-backed implementation.

#### Constructor

```csharp
public NpgsqlSensitiveDataStore(NpgsqlDataSource dataSource)
```

---

### SensitiveDataExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.Gdpr`

```csharp
public static IServiceCollection AddSensitiveDataStore(this IServiceCollection services)
```

Registers `NpgsqlSensitiveDataStore` as both `ISensitiveDataStore` and `ISensitiveDataResolver` (singleton). Already included in `AddPapumaKernel`.

---

### RedactionResult

**Kind**: sealed record
**Namespace**: `Papuma.Kernel.Gdpr`

```csharp
public sealed record RedactionResult(
    int EventsRedacted
);
```

| Property | Type | Description |
|----------|------|-------------|
| `EventsRedacted` | `int` | Number of `papuma_event_feed` rows redacted. |

---

### EntityHistory

**Kind**: sealed record
**Namespace**: `Papuma.Kernel.Gdpr`

```csharp
public sealed record EntityHistory(
    IReadOnlyList<ChangeRecord> Records
);
```

Represents the stored history for one entity from the unified event feed. Both Change and Event records are returned, distinguished by `record.Kind`.

---

### GdprProcessor

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Gdpr`
**Lifetime**: Singleton (registered by `AddPapumaKernel`)

Provides GDPR-oriented history and redaction operations. Callers should enforce authorization and rate-limiting before invoking redaction methods.

#### Constructor

```csharp
public GdprProcessor(
    NpgsqlDataSource dataSource,
    ChangeWriter businessEventWriter,
    ILogger<GdprProcessor> logger)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `dataSource` | `NpgsqlDataSource` | Yes | Data source for redaction and history queries. |
| `businessEventWriter` | `ChangeWriter` | Yes | Writer used to audit redaction operations. |
| `logger` | `ILogger<GdprProcessor>` | Yes | Logger for operational diagnostics. |

#### Methods

```csharp
public async Task<RedactionResult> RedactEntityAsync(
    ScopeContext scope,
    string entity,
    string entityId,
    string actorId,
    string reason,
    CancellationToken ct = default)
```

Redacts all change feed and business event log entries for a specific entity and scope. Overwrites payloads with `{"redacted": true}`, sets `redacted = TRUE`, and writes an `EntityRedacted` business event as audit trail. Runs in its own transaction (commits independently).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scope` | `ScopeContext` | Yes | The scope context. |
| `entity` | `string` | Yes | Entity name (validated). |
| `entityId` | `string` | Yes | Entity identifier (validated). |
| `actorId` | `string` | Yes | Actor performing the redaction (validated). |
| `reason` | `string` | Yes | Documented reason — must not be empty/whitespace. |
| `ct` | `CancellationToken` | No | — |

**Database tables affected**: `papuma_event_feed` (UPDATE), `papuma_event_feed` (UPDATE + INSERT audit event).

```csharp
public async Task<EntityHistory> GetEntityHistoryAsync(
    ScopeContext scope,
    string entity,
    string entityId,
    CancellationToken ct = default)
```

Returns the stored history for one entity and scope across change feed and business event log.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scope` | `ScopeContext` | Yes | The scope context. |
| `entity` | `string` | Yes | Entity name (validated). |
| `entityId` | `string` | Yes | Entity identifier (validated). |
| `ct` | `CancellationToken` | No | — |

---

### RetentionWorkerOptions

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Gdpr`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PollInterval` | `TimeSpan` | `1 hour` | Idle wait time between cleanup cycles. |
| `RetentionWindow` | `TimeSpan` | `365 days` | Redacted rows older than now minus this window are eligible for deletion. |
| `BatchSize` | `int` | `1000` | Max rows deleted per cycle. |

---

### RetentionWorker

**Kind**: sealed class (extends `BackgroundService`)
**Namespace**: `Papuma.Kernel.Gdpr`

Deletes redacted records older than the configured retention window.

#### Constructor

```csharp
public RetentionWorker(
    NpgsqlDataSource dataSource,
    ILogger<RetentionWorker> logger,
    RetentionWorkerOptions? options = null,
    ScopeFilter? scopeFilter = null)
```

---

### RetentionExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.Gdpr`

```csharp
public static IServiceCollection AddRetentionWorker(
    this IServiceCollection services,
    Action<RetentionWorkerOptions>? configure = null,
    ScopeFilter? scopeFilter = null)
```

Registers a `RetentionWorker` as a hosted service.

---

## Papuma.Kernel.Schema

### SchemaVersionChecker

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.Schema`
**Lifetime**: Singleton (registered by `AddPapumaKernel` or `AddSchemaVersionChecker`)

Checks whether the database schema has reached a required migration version.

**Constant**: `CurrentRequiredVersion = 5`

#### Constructor

```csharp
public SchemaVersionChecker(NpgsqlDataSource dataSource)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `dataSource` | `NpgsqlDataSource` | Yes | The data source used to query schema version metadata. |

#### Methods

```csharp
public async Task<int> GetCurrentVersionAsync(CancellationToken ct = default)
```

Gets the current schema version from `papuma_schema_version`. Returns `0` when the tracking table is missing.

```csharp
public async Task EnsureMinimumVersionAsync(int minimumVersion, CancellationToken ct = default)
```

Ensures the database schema version is at least the required version. Throws `InvalidOperationException` if current version is lower.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `minimumVersion` | `int` | Yes | The minimum required version. Must be >= 1. |
| `ct` | `CancellationToken` | No | — |

**Throws**: `ArgumentOutOfRangeException` if `minimumVersion < 1`, `InvalidOperationException` if current version < minimum.

```csharp
public async Task EnsureCurrentBaselineAsync(CancellationToken ct = default)
```

Ensures the database schema version matches the library's required baseline (`CurrentRequiredVersion = 5`). Equivalent to `EnsureMinimumVersionAsync(4, ct)`.

**Database table**: `papuma_schema_version`

---

### SchemaExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.Schema`

```csharp
public static IServiceCollection AddSchemaVersionChecker(this IServiceCollection services)
```

Registers `SchemaVersionChecker` as singleton. Already included in `AddPapumaKernel`.

---

## Papuma.Kernel.Validation

### InputValidator

**Kind**: static class
**Namespace**: `Papuma.Kernel.Validation`

Provides shared input validation for change feed and event writers. All methods throw `ArgumentException` on invalid input.

#### Methods

```csharp
public static void ValidateEntity(string entity)
```

Validates an entity name against pattern `[A-Za-z][A-Za-z0-9_]{1,100}`.

```csharp
public static void ValidateEntityId(string entityId)
```

Validates: non-empty, max 200 characters.

```csharp
public static void ValidateEventType(string eventType)
```

Validates against pattern `[A-Za-z][A-Za-z0-9_]{2,100}`.

```csharp
public static void ValidateActorId(string actorId)
```

Validates: non-empty, max 200 characters.

```csharp
public static void ValidatePayloadSize(string payloadJson, int maxPayloadSizeBytes)
```

Validates that UTF-8 byte count does not exceed `maxPayloadSizeBytes`. Throws `ArgumentNullException` if `payloadJson` is null.

```csharp
public static void ValidateVersion(int version)
```

Validates: version >= 1.

```csharp
public static void ValidateIdempotencyKey(string idempotencyKey)
```

Validates: non-empty, max 200 characters. (Only called when the key is not null.)

---

## Papuma.Kernel.AspNetCore.Tenancy

### IScopeResolver

**Kind**: interface
**Namespace**: `Papuma.Kernel.AspNetCore.Tenancy`
**Lifetime**: Scoped (registered by `AddPapumaScope<TResolver>`)

Resolves the scope context from an ASP.NET Core request. **Implement this interface** to determine Platform vs Tenant scope based on the HTTP request (e.g., from JWT claims, host header, or route parameter).

#### Methods

```csharp
ScopeContext Resolve(HttpContext context)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `context` | `HttpContext` | The current HTTP context. |

**Returns**: A `ScopeContext` with the resolved scope and optionally a tenant ID.

---

### ScopeMiddleware

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.AspNetCore.Tenancy`

Resolves and stores the scope context for the current HTTP request. Registered by `UseScopeResolution()`.

#### Constructor

```csharp
public ScopeMiddleware(RequestDelegate next)
```

#### InvokeAsync

```csharp
public async Task InvokeAsync(HttpContext context, IScopeResolver resolver)
```

Resolves scope context via `resolver.Resolve(context)` and stores it in `HttpContext.Items` under key `"Papuma.Kernel.Tenancy.ScopeContext"`.

---

### ScopeMiddlewareExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.AspNetCore.Tenancy`

#### Methods

```csharp
public static IServiceCollection AddPapumaScope<TResolver>(this IServiceCollection services)
    where TResolver : class, IScopeResolver
```

Registers `TResolver` as the `IScopeResolver` implementation with scoped lifetime.

```csharp
public static IApplicationBuilder UseScopeResolution(this IApplicationBuilder app)
```

Adds `ScopeMiddleware` to the request pipeline. Must be called after `AddPapumaScope<TResolver>()`.

```csharp
public static ScopeContext GetScopeContext(this HttpContext context)
```

Gets the resolved scope context for the current request. Throws `InvalidOperationException` if no scope context was set (i.e., `UseScopeResolution()` was not called or the middleware didn't run).

| Parameter | Type | Description |
|-----------|------|-------------|
| `context` | `HttpContext` | The current HTTP context. |

---

## Papuma.Kernel.AspNetCore.Projections

### ProjectionHealthCheckOptions

**Kind**: sealed class
**Namespace**: `Papuma.Kernel.AspNetCore.Projections`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxAllowedLag` | `long` | `1000` | Maximum tolerated lag before the health check becomes unhealthy. |

---

### ProjectionLagHealthCheck

**Kind**: sealed class (implements `IHealthCheck`)
**Namespace**: `Papuma.Kernel.AspNetCore.Projections`

Reports unhealthy status when one or more projection workers exceed the configured lag threshold.

#### Constructor

```csharp
public ProjectionLagHealthCheck(
    IEnumerable<IHostedService> hostedServices,
    IOptions<ProjectionHealthCheckOptions> options)
```

Auto-discovers all `IProjectionLagProvider` instances from the `IHostedService` collection.

**Health check name**: `"papuma.projections"`
**Status on failure**: `HealthStatus.Unhealthy`

---

### ProjectionHealthCheckExtensions

**Kind**: static class
**Namespace**: `Papuma.Kernel.AspNetCore.Projections`

```csharp
public static IServiceCollection AddPapumaProjectionHealthChecks(
    this IServiceCollection services,
    Action<ProjectionHealthCheckOptions>? configure = null)
```

Registers projection lag health checks. Adds a health check named `"papuma.projections"` that reports unhealthy when any projection's lag exceeds `MaxAllowedLag`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `services` | `IServiceCollection` | Yes | The service collection. |
| `configure` | `Action<ProjectionHealthCheckOptions>?` | No | Optional configuration callback. |

---

# Code Examples

## Example: Complete Write Transaction

This is the canonical write path — domain state mutation + change feed + business event + outbox, all in one transaction:

```csharp
public class CreateOrderHandler
{
    private readonly IUnitOfWork _uow;
    private readonly ChangeWriter _changeWriter;
    private readonly ChangeWriter _eventWriter;
    private readonly OutboxWriter _outboxWriter;

    public CreateOrderHandler(
        IUnitOfWork uow,
        ChangeWriter changeWriter,
        ChangeWriter eventWriter,
        OutboxWriter outboxWriter)
    {
        _uow = uow;
        _changeWriter = changeWriter;
        _eventWriter = eventWriter;
        _outboxWriter = outboxWriter;
    }

    public async Task HandleAsync(
        ScopeContext scope,
        string orderId,
        decimal amount,
        string actorId,
        string? correlationId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        await _uow.ExecuteAsync(async (conn, tx, ct) =>
        {
            // 1. Write domain state (your own SQL)
            await using var domainCmd = conn.CreateCommand();
            domainCmd.Transaction = tx;
            domainCmd.CommandText =
                "INSERT INTO orders (order_id, amount, status) VALUES (@id, @amount, 'pending')";
            domainCmd.Parameters.AddWithValue("id", orderId);
            domainCmd.Parameters.AddWithValue("amount", amount);
            await domainCmd.ExecuteNonQueryAsync(ct);

            // 2. Append change feed record
            var payload = $$"""{"orderId":"{{orderId}}","amount":{{amount}}}""";
            await _changeWriter.AppendChangeAsync(
                tx, scope,
                entity: "Order",
                entityId: orderId,
                eventType: "OrderCreated",
                version: 1,
                payloadJson: payload,
                actorId: actorId,
                correlationId: correlationId,
                idempotencyKey: idempotencyKey,
                ct: ct);

            // 3. Write business event + enqueue outbox in one step
            var eventId = await _changeWriter.AppendEventAsync(
                tx, scope,
                eventType: "OrderPlaced",
                actorId: actorId,
                payloadJson: payload,
                entity: "Order",
                entityId: orderId,
                correlationId: correlationId,
                ct: ct);

            await _outboxWriter.EnqueueAsync(
                tx, scope,
                eventId: eventId,
                eventType: "OrderPlaced",
                payloadJson: payload,
                ct: ct);

        }, ct);
    }
}
```

---

## Example: In-Database Projection Handler

```csharp
using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;
using Npgsql;

public sealed class OrderSummaryProjection : IProjectionHandler
{
    public string Name => "OrderSummary";
    public IReadOnlyCollection<string> EventTypes => new[] { "OrderCreated", "OrderCancelled" };

    public async Task HandleAsync(
        ChangeRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = record.EventType switch
        {
            "OrderCreated" =>
                "INSERT INTO order_summaries (order_id, status, created_at) VALUES (@id, 'active', @ts)",
            "OrderCancelled" =>
                "UPDATE order_summaries SET status = 'cancelled' WHERE order_id = @id",
            _ => throw new InvalidOperationException($"Unknown event type: {record.EventType}")
        };

        cmd.Parameters.AddWithValue("id", record.EntityId);
        cmd.Parameters.AddWithValue("ts", record.Timestamp);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// Registration:
// builder.Services.AddProjection<OrderSummaryProjection>();
```

---

## Example: External Projection Handler (Search Index)

```csharp
using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

public sealed class SearchIndexProjection : IExternalProjectionHandler
{
    private readonly ISearchClient _searchClient;

    public string Name => "SearchIndex";

    public IReadOnlyCollection<string> EventTypes => new[]
    {
        "OrderCreated", "OrderUpdated", "OrderCancelled"
    };

    public SearchIndexProjection(ISearchClient searchClient)
    {
        _searchClient = searchClient;
    }

    public async Task HandleAsync(ChangeRecord record, CancellationToken ct = default)
    {
        await _searchClient.IndexAsync(new
        {
            Id = record.EntityId,
            Type = record.Entity,
            Action = record.EventType,
            Timestamp = record.Timestamp
        }, ct);
    }
}

// Registration:
// builder.Services.AddExternalProjection<SearchIndexProjection>();
```

---

## Example: Outbox Publisher (RabbitMQ)

```csharp
using Papuma.Kernel.Events;
using Papuma.Kernel.Tenancy;

public sealed class RabbitMqOutboxPublisher : IOutboxPublisher
{
    private readonly IChannel _channel;

    public RabbitMqOutboxPublisher(IChannel channel)
    {
        _channel = channel;
    }

    public async Task<bool> PublishAsync(
        ScopeContext scope,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(payloadJson);
            var properties = new BasicProperties
            {
                MessageId = eventId.ToString(),  // Idempotency via MessageId dedup
                Type = eventType,
                Headers = new Dictionary<string, object?>
                {
                    ["scope"] = scope.Scope.ToString(),
                    ["tenant_id"] = scope.TenantId ?? ""
                }
            };

            await _channel.BasicPublishAsync(
                exchange: "papuma.events",
                routingKey: eventType,
                mandatory: true,
                basicProperties: properties,
                body: body,
                cancellationToken: ct);

            return true;
        }
        catch (Exception)
        {
            return false;  // Retry
        }
    }
}

// Registration:
// builder.Services.AddSingleton<IChannel>(...);
// builder.Services.AddOutboxWorker<RabbitMqOutboxPublisher>();
```

---

## Example: ASP.NET Core Scope Resolution

```csharp
using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Tenancy;

// Resolver: determines scope from JWT claims
public sealed class JwtScopeResolver : IScopeResolver
{
    public ScopeContext Resolve(HttpContext context)
    {
        var user = context.User;

        if (user.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Authentication required.");

        var scopeClaim = user.FindFirst("scope")?.Value;
        var tenantClaim = user.FindFirst("tenant_id")?.Value;

        return scopeClaim switch
        {
            "Platform" => ScopeContext.Platform(),
            "Tenant" when tenantClaim is not null => ScopeContext.Tenant(tenantClaim),
            _ => throw new UnauthorizedAccessException("Invalid scope claim.")
        };
    }
}

// Registration:
// builder.Services.AddPapumaScope<JwtScopeResolver>();
// ...
// app.UseScopeResolution();

// Usage in controller:
[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderCommand cmd, CancellationToken ct)
    {
        var scope = HttpContext.GetScopeContext();
        // scope is now ScopeContext.Platform() or ScopeContext.Tenant("acme")
        await _handler.HandleAsync(scope, cmd, ct);
        return Ok();
    }
}
```

---

## Example: GDPR Redaction

```csharp
public class GdprRedactionHandler
{
    private readonly GdprProcessor _gdpr;

    public GdprRedactionHandler(GdprProcessor gdpr)
    {
        _gdpr = gdpr;
    }

    public async Task<RedactionResult> RedactUserDataAsync(
        string userId,
        string adminActorId,
        CancellationToken ct)
    {
        // Each tenant scope must be redacted separately if applicable
        var scope = ScopeContext.Platform();

        var result = await _gdpr.RedactEntityAsync(
            scope,
            entity: "UserProfile",
            entityId: userId,
            actorId: adminActorId,
            reason: "GDPR Article 17 — Right to erasure, request #REQ-2026-042",
            ct: ct);

        // result.EventsRedacted — papuma_event_feed rows redacted
        // An EntityRedacted audit event is written automatically

        return result;
    }

    public async Task<EntityHistory> GetUserHistoryAsync(
        string userId,
        CancellationToken ct)
    {
        var scope = ScopeContext.Platform();
        return await _gdpr.GetEntityHistoryAsync(scope, "UserProfile", userId, ct);
    }
}
```

---

## Example: Sensitive Data Indirection

Instead of embedding PII in every event payload, use `SensitiveRef`:

```csharp
public class ProfileWriteHandler
{
    private readonly ISensitiveDataStore _sensitiveStore;
    private readonly ISensitiveDataResolver _sensitiveResolver;
    private readonly ChangeWriter _changeWriter;

    public ProfileWriteHandler(
        ISensitiveDataStore sensitiveStore,
        ISensitiveDataResolver sensitiveResolver,
        ChangeWriter changeWriter)
    {
        _sensitiveStore = sensitiveStore;
        _sensitiveResolver = sensitiveResolver;
        _changeWriter = changeWriter;
    }

    public async Task WriteProfileAsync(
        NpgsqlTransaction tx,
        ScopeContext scope,
        string userId,
        string email,
        string actorId,
        CancellationToken ct)
    {
        // 1. Store sensitive PII separately
        var sensitiveRef = SensitiveRef.New();
        await _sensitiveStore.AppendAsync(
            scope,
            sensitiveRef,
            schemaVersion: 1,
            payloadJson: $$"""{"email":"{{email}}"}""",
            actorId: actorId,
            ct: ct);

        // 2. Write change feed with ONLY the reference
        await _changeWriter.AppendChangeAsync(
            tx, scope,
            entity: "Profile",
            entityId: userId,
            eventType: "ProfileEmailUpdated",
            version: 1,
            payloadJson: $$"""{"sensitiveRef":"{{sensitiveRef.Value}}"}""",
            actorId: actorId,
            ct: ct);
    }

    public async Task<string?> ReadEmailAsync(
        ScopeContext scope,
        string sensitiveRefGuid,
        CancellationToken ct)
    {
        var sensitiveRef = new SensitiveRef(Guid.Parse(sensitiveRefGuid));
        return await _sensitiveResolver.TryResolveLatestPayloadAsync(
            scope, sensitiveRef, ct);
        // Returns null if redacted or deleted
    }
}
```

---

## Example: Schema Version Check at Startup

```csharp
// In Program.cs, after building the app but before Run():
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var checker = scope.ServiceProvider.GetRequiredService<SchemaVersionChecker>();
    await checker.EnsureCurrentBaselineAsync();
    // Throws InvalidOperationException if DB schema is behind version 5
}

app.Run();
```

---

## Example: Projection Lag Health Check

```csharp
builder.Services.AddPapumaProjectionHealthChecks(options =>
{
    options.MaxAllowedLag = 500;  // Unhealthy if any projection is 500+ events behind
});

var app = builder.Build();
app.MapHealthChecks("/health");
// Returns 503 when projection lag exceeds threshold
```

---

# Appendices

## Validation Rules Reference

| Input | Rule | Error |
|-------|------|-------|
| `entity` | `[A-Za-z][A-Za-z0-9_]{1,100}` | `ArgumentException("entity")` |
| `entityId` | Non-empty, max 200 chars | `ArgumentException("entityId")` |
| `eventType` | `[A-Za-z][A-Za-z0-9_]{2,100}` | `ArgumentException("eventType")` |
| `actorId` | Non-empty, max 200 chars | `ArgumentException("actorId")` |
| `version` | >= 1 | `ArgumentException("version")` |
| `payloadJson` | UTF-8 bytes <= `MaxPayloadSizeBytes` (default 256 KB) | `ArgumentException("payloadJson")` |
| `idempotencyKey` | If provided: non-empty, max 200 chars | `ArgumentException("idempotencyKey")` |
| `tenantId` | `[A-Za-z][A-Za-z0-9_]{1,100}` | `ArgumentException("tenantId")` |
| `reason` (GDPR) | Non-empty, non-whitespace | `ArgumentException("reason")` |
| `limit` (reader) | >= 1 | `ArgumentOutOfRangeException("limit")` |
| `fromSequenceId` | >= 1 | `ArgumentOutOfRangeException("fromSequenceId")` |
| `toSequenceId` | >= `fromSequenceId` | `ArgumentOutOfRangeException("toSequenceId")` |

---

## Database Tables Reference

### Framework-Managed Tables

| Table | Purpose | Key Columns |
|-------|---------|-------------|
| `papuma_event_feed` | Append-only event log for state transitions | `sequence_id`, `scope`, `tenant_id`, `entity`, `entity_id`, `event_type`, `version`, `payload` (JSONB), `actor_id`, `correlation_id`, `causation_id`, `timestamp`, `redacted`, `idempotency_key` |
| `papuma_event_feed` | Semantic business events for auditing and integration | `event_id` (UUID), `scope`, `tenant_id`, `event_type`, `entity`, `entity_id`, `actor_id`, `payload` (JSONB), `correlation_id`, `causation_id`, `occurred_at`, `redacted`, `idempotency_key` |
| `papuma_event_outbox` | Transactional outbox for reliable message publishing | `id`, `scope`, `tenant_id`, `event_id`, `event_type`, `payload` (JSONB), `created_at`, `published`, `attempt_count`, `last_error`, `next_retry_at` |
| `papuma_projection_checkpoint` | Per-projection read position tracking | `projection_name`, `last_sequence_id` |
| `papuma_projection_failures` | Dead-letter store for failed projection events | `projection_name`, `sequence_id`, `event_type`, `error_message`, `failed_at` |
| `papuma_sensitive_data_versions` | Versioned sensitive payload storage (GDPR) | `sensitive_ref` (UUID), `version`, `schema_version`, `payload` (JSONB), `state` (Active/Redacted/Deleted), `legal_hold`, `actor_id`, `created_at`, `reason` |
| `papuma_schema_version` | Schema migration tracking | `version`, `applied_at` |

### Unique Constraints

| Table | Constraint | Columns |
|-------|-----------|---------|
| `papuma_event_feed` | `ux_papuma_event_feed_idempotency_key` | `(scope, tenant_id, idempotency_key)` |
| `papuma_event_feed` | `ux_papuma_event_feed_idempotency_key` | `(scope, tenant_id, idempotency_key)` |

### Row-Level Security

All framework tables use PostgreSQL RLS policies driven by session variables `app.current_scope` and `app.current_tenant` (set via `ScopeConnectionExtensions.SetScopeAsync`).

---

## Transient SQL States (Retry Triggers)

The `NpgsqlUnitOfWork` retries on these PostgreSQL error codes:

| SQL State | Name | Description |
|-----------|------|-------------|
| `40001` | serialization_failure | Serializable isolation conflict |
| `40P01` | deadlock_detected | Deadlock detected |
| `08006` | connection_failure | Connection failure |
| `08001` | sqlclient_unable_to_establish_sqlconnection | Cannot connect |
| `57P03` | cannot_connect_now | Database rejecting connections |

---

## Default Values Quick Reference

| Option | Property | Default |
|--------|----------|---------|
| `PapumaKernelOptions` | `MaxPayloadSizeBytes` | `262144` (256 KB) |
| `PapumaKernelOptions` | `UnitOfWork` | `null` (uses `UnitOfWorkOptions` defaults) |
| `ChangeWriterOptions` | `MaxPayloadSizeBytes` | `262144` |
| `ChangeWriterOptions` | `MaxPayloadSizeBytes` | `262144` |
| `OutboxWriterOptions` | `MaxPayloadSizeBytes` | `262144` |
| `UnitOfWorkOptions` | `MaxRetries` | `3` |
| `UnitOfWorkOptions` | `BaseRetryDelay` | `100 ms` |
| `ProjectionWorkerOptions` | `PollInterval` | `100 ms` |
| `ProjectionWorkerOptions` | `BatchSize` | `100` |
| `ProjectionWorkerOptions` | `MaxAttemptsPerEvent` | `10` |
| `ProjectionWorkerOptions` | `BaseRetryDelay` | `5 sec` |
| `ProjectionWorkerOptions` | `MaxRetryDelay` | `5 min` |
| `OutboxWorkerOptions` | `PollInterval` | `100 ms` |
| `OutboxWorkerOptions` | `BatchSize` | `100` |
| `OutboxWorkerOptions` | `MaxAttemptsPerMessage` | `10` |
| `OutboxWorkerOptions` | `BaseRetryDelay` | `5 sec` |
| `OutboxWorkerOptions` | `MaxRetryDelay` | `5 min` |
| `RetentionWorkerOptions` | `PollInterval` | `1 hour` |
| `RetentionWorkerOptions` | `RetentionWindow` | `365 days` |
| `RetentionWorkerOptions` | `BatchSize` | `1000` |
| `ProjectionHealthCheckOptions` | `MaxAllowedLag` | `1000` |
| `SchemaVersionChecker` | `CurrentRequiredVersion` | `4` |

---

## DI Registration Summary

| Method | Registers | Lifetime |
|--------|-----------|----------|
| `AddPapumaKernel(...)` | `ChangeWriter`, `ChangeFeedReader`, `OutboxWriter`, `GdprProcessor`, `SchemaVersionChecker`, `ISensitiveDataStore`, `ISensitiveDataResolver`, `IUnitOfWork` | All Singleton |
| `AddChangeFeedReader()` | `ChangeFeedReader` | Singleton |
| `AddProjection<THandler>(...)` | `THandler` + `ProjectionWorker` (as `IHostedService`) | Singleton |
| `AddExternalProjection<THandler>(...)` | `THandler` + `ExternalProjectionWorker` (as `IHostedService`) | Singleton |
| `AddReplayService()` | `ReplayService` | Singleton |
| `AddOutboxWorker<TPublisher>(...)` | `TPublisher` + `OutboxWorker` (as `IHostedService`) | Singleton |
| `AddRetentionWorker(...)` | `RetentionWorker` (as `IHostedService`) | Singleton |
| `AddSensitiveDataStore()` | `NpgsqlSensitiveDataStore` as `ISensitiveDataStore` and `ISensitiveDataResolver` | Singleton |
| `AddSchemaVersionChecker()` | `SchemaVersionChecker` | Singleton |
| `AddPapumaScope<TResolver>()` | `TResolver` as `IScopeResolver` | **Scoped** |
| `UseScopeResolution()` | `ScopeMiddleware` | (middleware) |
| `AddPapumaProjectionHealthChecks(...)` | `ProjectionLagHealthCheck` + configuration | Singleton |

---

> For architectural design rules, philosophy, anti-patterns, and AI decision guidance, see the companion document: [Papuma.Kernel AI Framework Context](papuma-kernel-ai-framework-context.md).
rk Context](papuma-kernel-ai-framework-context.md).
nt: [Papuma.Kernel AI Framework Context](papuma-kernel-ai-framework-context.md).
rk Context](papuma-kernel-ai-framework-context.md).
ext.md).
nt: [Papuma.Kernel AI Framework Context](papuma-kernel-ai-framework-context.md).
rk Context](papuma-kernel-ai-framework-context.md).
