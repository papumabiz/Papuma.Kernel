# Papuma Kernel — Architecture Overview

Status: current (2.0.x) · Background: [concepts.md](concepts.md) (the "why behind the how", narrative)

> **Describes the PostgreSQL kernel.** Engine differences of `Papuma.Kernel.Local`
> (SQLite) are in the playbook's [Differences section](ai/papuma-kernel-playbook.md#differences-when-using-papumakernellocal-sqlite-embedded).

This document describes the reboot of Papuma.Kernel as **document-sourced CQRS**:
JSON documents are the truth, the change feed arises automatically as a diff,
projections are deliberately dumb change handlers. There is no migration path
from v1 — 1.0 was a fresh start (derived in the original design discussion,
"chat-1"; removed with the v1 cleanup).

The binding individual decisions live in [adr/](adr/) — this document is the map
above them.

---

## 1. Problem statement

Papuma's actual problem was never "how do I store data?", but:

> How do I get changes as a first-class concept without an event-sourcing
> mandate?

v1 solved this with relational tables, an outbox and explicitly produced events —
at the familiar price: change detection is laborious, events must be defined
manually, GDPR-relevant data ends up in immutable feeds.

Papuma Kernel inverts the model:

```text
Entity (C# class)
      ↓
JSON document (truth)
      ↓
Change kernel (diff, version, policies)
      ↓
PostgreSQL (document + change feed, one transaction)
      ↓
Change handlers (projections, search, audit, integration)
```

Contrast with Marten: there, the **event** is the truth and state is derived.
With Papuma, the **document** is the truth and the change stream is derived.
Conceptually closer to "git for aggregates" or the Cosmos DB change feed than to
event sourcing.

---

## 2. Guiding principles

1. **The document is the truth.** The current state exists as a JSONB document;
   the change feed is derived, not the other way around
   ([ADR-002](adr/adr-002-document-as-truth.md)).
2. **PostgreSQL ≥ 18, without a provider abstraction.** The kernel deliberately
   exploits JSONB, expression indexes, LISTEN/NOTIFY and `RETURNING OLD/NEW`
   ([ADR-001](adr/adr-001-postgresql-18-only.md)).
3. **One write = one transaction = one atomic roundtrip.** Optimistic concurrency
   via a `version` column is a kernel invariant, not an implementation detail
   ([ADR-003](adr/adr-003-write-path-concurrency.md)).
4. **The change feed stores diffs, not snapshots.** Reversible, lean,
   policy-capable ([ADR-004](adr/adr-004-changerecord-diff-only.md)).
5. **Schema evolution is a day-one concept.** Every document carries a
   `schema_version`; upcasters lift old documents at load time
   ([ADR-005](adr/adr-005-schema-evolution.md)).
6. **Constraints come back under control.** Uniqueness and lookup keys are
   declared in the metamodel and materialized as JSONB expression indexes — not
   ad hoc ([ADR-006](adr/adr-006-keys-and-constraints.md)).
7. **Privacy is a kernel responsibility.** Policies (redact, reference,
   do-not-track, hash) are applied when the diff is produced, before anything
   reaches the feed ([ADR-007](adr/adr-007-privacy-policies.md)).
8. **No domain events in the storage layer.** The kernel only knows
   `DocumentChanged`; `OrderPaid` arises — if at all — in the processing layer
   ([ADR-011](adr/adr-011-no-business-events-in-storage.md)).
9. **Projections are dumb, the engine is infrastructure.** No SQL generator, no
   read-model DSL ([ADR-009](adr/adr-009-projections-as-dumb-handlers.md)).

---

## 3. Layers and project structure

Papuma Kernel remains a small library (one package plus optional ASP.NET Core
integration); the layers are namespaces, not separate NuGet packages:

```text
Papuma.Kernel
├── Papuma.Kernel.Store        load/save/delete documents, write path, session
├── Papuma.Kernel.Changes      ChangeRecord, diff engine, policies
├── Papuma.Kernel.Events       event log: append, EventFeedProcessor, retention (ADR-013)
├── Papuma.Kernel.Processing   change-handler engine: checkpoints, retry, rebuild
├── Papuma.Kernel.Model        metamodel: types, keys, policies, schema versions
└── Papuma.Kernel.Hosting      AddPapumaKernel bootstrap, schema contributors, hosted feed workers

Papuma.Kernel.AspNetCore       tenant resolution, change-feed-lag health check
Papuma.Kernel.Testing          test database with a non-superuser role, feed draining (ADR-021)
```

The split into `Store / Changes / Processing` follows the sketch from the design
discussion ("Papuma.Store / Papuma.ChangeFeed / Papuma.Processing") — but without package
splitting, which would be premature abstraction.

---

## 4. Data model (PostgreSQL)

```sql
CREATE TABLE papuma.document
(
    scope           text        NOT NULL,   -- 'Platform' | 'Tenant' (scope model from v1)
    tenant_id       text        NOT NULL DEFAULT '',  -- empty for platform scope
    document_type   text        NOT NULL,   -- logical aggregate name from the metamodel
    id              text        NOT NULL,
    version         bigint      NOT NULL,   -- optimistic concurrency, starts at 1
    schema_version  int         NOT NULL,   -- state of the C# class at the last write
    data            jsonb       NOT NULL,
    created_by      text        NOT NULL DEFAULT '',  -- actor who created the document (ADR-017)
    updated_by      text        NOT NULL DEFAULT '',  -- actor of the most recent write (ADR-017)
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (scope, tenant_id, document_type, id)
);

CREATE TABLE papuma.change
(
    seq             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope           text        NOT NULL,
    tenant_id       text        NOT NULL DEFAULT '',
    document_type   text        NOT NULL,
    document_id     text        NOT NULL,
    version         bigint      NOT NULL,   -- document version AFTER the change
    schema_version  int         NOT NULL,
    operation       smallint    NOT NULL,   -- 1=insert, 2=update, 3=delete
    diff            jsonb       NOT NULL,   -- see ADR-004 (reversible field diff)
    actor_id        text        NOT NULL DEFAULT '',  -- who caused this change (ADR-017)
    metadata        jsonb       NOT NULL,   -- isRollback, restoredVersion, correlationId, ...
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    txid            xid8        NOT NULL DEFAULT pg_current_xact_id()  -- snapshot cursor, ADR-022
);

CREATE UNIQUE INDEX ux_papuma_change_document_version
    ON papuma.change (scope, tenant_id, document_type, document_id, version);

CREATE INDEX ix_papuma_change_correlation          -- GetChangesByCorrelationAsync
    ON papuma.change (scope, tenant_id, (metadata ->> 'correlationId'));

CREATE INDEX ix_papuma_change_txid ON papuma.change (txid);  -- slice reads, ADR-022
```

Both tables carry row-level-security policies (including the `'All'` scope for
workers and `FORCE ROW LEVEL SECURITY`); the binding DDL lives in `SchemaDdl.cs`.

Unique keys and lookup columns per document type are created as partial
expression indexes from the metamodel (`SchemaManager.BuildKeyIndexDdl`) — one
expression per key field, so a composite key (ADR-020) is one multi-column index:

```sql
CREATE UNIQUE INDEX IF NOT EXISTS ux_papuma_doc_user_email
    ON papuma.document (scope, tenant_id, (data #>> '{email}'))
    WHERE document_type = 'User';

CREATE UNIQUE INDEX IF NOT EXISTS ux_papuma_doc_ticket_projectid__number
    ON papuma.document (scope, tenant_id, (data #>> '{projectId}'), (data #>> '{number}'))
    WHERE document_type = 'Ticket';
```

The schema also provides `papuma.scope_visible` / `papuma.scope_writable` — the
scope rules of these policies as SQL functions, the contract for RLS on
application tables (ADR-019).

---

## 5. The write path

The core of the design: **a single atomic statement delivers the old and the new
state**, thanks to PostgreSQL ≥ 18 `RETURNING OLD/NEW`. No prior load, no second
roundtrip, no window for race conditions.

```sql
-- update with optimistic concurrency
UPDATE papuma.document
SET data           = @data,
    version        = version + 1,
    schema_version = @schemaVersion,
    updated_at     = now()
WHERE tenant_id = @tenantId
  AND document_type = @type
  AND id = @id
  AND version = @expectedVersion
RETURNING old.data AS old_data, new.data AS new_data, new.version;
```

- **0 rows** → `ConcurrencyException` (someone else wrote in the meantime, or the
  document does not exist).
- **1 row** → the kernel diffs `old_data` against `new_data` in C#, applies the
  privacy policies and writes the `ChangeRecord` **in the same transaction**.

Insert (`old` is NULL) and delete (`new` is NULL, `DELETE ... RETURNING old.data`)
follow the same pattern. Details and the API sketch:
[ADR-003](adr/adr-003-write-path-concurrency.md).

The public API stays small:

```csharp
DocumentSession session = store.OpenSession(tenant);

SaveResult<User> result = await session.SaveAsync(user, expectedVersion);
// result.Version, result.Operation, result.Diff (policy-applied)

await session.DeleteAsync<User>(id, expectedVersion);
User? current = await session.LoadAsync<User>(id);          // incl. upcasting
```

### Partial updates (patch)

For single-field changes there is a second write primitive — **without a prior
load**, neither by the caller nor internally:

```csharp
await session.PatchAsync<User>(id, p => p.Set(x => x.DisplayName, "Harry"));
```

The "read" happens inside the `UPDATE` itself (`jsonb_set` +
`RETURNING old.data, new.data`); diff, policies and ChangeRecord arise as with a
save. Concurrency is opt-in for patches (field-level last-writer-wins without
`expectedVersion`). Details and the limits of the operation catalog:
[ADR-012](adr/adr-012-partial-updates.md).

### Session = unit of work

A `DocumentSession` bundles several writes into **one** Postgres transaction
(opened lazily; every write runs under a savepoint, so a typed failure does not
discard earlier writes; dispose without commit rolls back):

```csharp
await using var session = store.OpenSession(tenant);
await session.SaveAsync(user, expectedVersion: 0);      // registration: two aggregates,
await session.SaveAsync(address, expectedVersion: 0);   // one atomic commit
await session.CommitAsync();
```

Documents and ChangeRecords of all writes become visible atomically. Consumers
see one ChangeRecord per document; a shared `correlationId` in the change
metadata connects a session's writes at the domain level. The modeling rule of
thumb is unaffected: *embed by default* — what must be consistent together
belongs in **one** document; separate aggregates only for separate lifecycles.

Rollback is deliberately **not its own operation type** but an update with
`metadata.IsRollback = true` ([ADR-008](adr/adr-008-rollback-is-update.md)).

---

## 6. Metamodel

At startup (reflection for now, later optionally a source generator) the kernel
builds a complete metamodel per registered document type:

> **Source generator — when, not if:** the reflection scan runs once at startup
> (milliseconds); runtime performance is not an SG argument. The fluent overrides
> remain runtime per ADR-007 anyway (policies changeable without recompiling) —
> an SG can only precompute the attribute part. Triggers for the switch:
> (a) NativeAOT/trimming as a target (then together with the STJ
> `JsonSerializerContext`), (b) compile-time diagnostics as DX polish. Since
> `KernelModel` is an immutable data structure, swapping the build source is
> invisible to all consumers — the decision is safely deferred.

```csharp
DocumentTypeMetadata
{
    Name           = "User",
    ClrType        = typeof(User),
    SchemaVersion  = 3,                    // highest registered upcaster + 1
    Keys           = [ UniqueKey("email") ],
    Properties     =
    [
        { Path = "email",     Policy = Reference },
        { Path = "phone",     Policy = Redact },
        { Path = "lastSeen",  Policy = DoNotTrack },
        { Path = "name",      Policy = Track }      // default
    ]
}
```

Sources of the metamodel, in this priority order:

1. **Fluent configuration** at store setup (organization-specific overrides),
2. **Attributes** on the C# class (`[SensitiveData]`, `[DoNotTrack]`,
   `[TrackHash]`, `[UniqueKey]`) as defaults at the place of truth,
3. Convention (everything is tracked).

Rationale and the attribute/policy catalog:
[ADR-007](adr/adr-007-privacy-policies.md), keys:
[ADR-006](adr/adr-006-keys-and-constraints.md).

---

## 7. Schema evolution

The C# class is the truth of *today* — the database holds documents from
*yesterday*. Therefore ([ADR-005](adr/adr-005-schema-evolution.md)):

- Every document and every ChangeRecord carries `schema_version`.
- Per type, upcasters are registered that lift JSON from version n to n+1:

  ```csharp
  builder.For<User>()
      .Upcast(fromVersion: 1, json => { json["email"] = json["mail"]; json.Remove("mail"); });
  ```

- Upcasting happens **at load time** (lazily); the document is physically written
  to the new state only on the next `Save`.
- ChangeRecords are **never** migrated retroactively — consumers of old changes
  must handle the historical `schema_version` (or run the replay through the
  upcasters).

---

## 8. Change consumption and projections

### Handler model

```csharp
public interface IChangeHandler
{
    string Name { get; }
    Task HandleAsync(ChangeRecord change, CancellationToken ct);
}
```

Nothing more. A handler can be a SQL projection, a search index update, a
webhook, an audit log, an event translator. The kernel generates no SQL and knows
no read models ([ADR-009](adr/adr-009-projections-as-dumb-handlers.md)).

The engine provides the infrastructure:

- **Ordering**: per handler in causal order — a transaction after every one that
  committed before it began (concurrent ones unordered), per document strictly by
  `version`. `seq` identifies a change; it is not a watermark. Handlers must not decide
  from the order of unrelated documents (concepts §2).
- **Checkpoints**: one persisted cursor per handler (`papuma.checkpoint`).
- **Retry** with backoff and poison handling.
- **Rebuild**: projections declare themselves (`IProjection`: `Version`, `ResetAsync`);
  a raised version is rebuilt once at the next start, `ResetProjectionsAsync()` rebuilds
  all; effects can start at the head (`[StartsAtFeedHead]`) — ADR-024.

### Reading by snapshot cursor

A naive `WHERE seq > @lastSeq` loses changes whose transaction commits later than
one with a higher `seq` — and no rule over a `seq` checkpoint is safe, because it
would need the sequence numbers of still-open transactions. A handler's position is
therefore a transaction snapshot (PgQ model): each cycle delivers, as a *slice*, the
rows of every transaction visible in the current snapshot but not in the last
completed one, in `seq` order; an exhausted slice becomes the new position. Nothing
can commit behind the cursor, and an open transaction holds back only its own rows
([ADR-022](adr/adr-022-snapshot-cursor.md), superseding the `txid < xmin` horizon
of ADR-010). Lag is the count of committed, undelivered rows. LISTEN/NOTIFY serves
only as the wakeup; polling remains the truth
([ADR-010](adr/adr-010-feed-consumption.md), building on
[polling-vs-listen-analysis.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/legacy/polling-vs-listen-analysis.md)).

### Convenience on top, not underneath

Helpers such as

```csharp
WhenFieldChanged<User>(x => x.Email)
```

are thin filters over `ChangeRecord.Diff` — sugar above the handler interface,
not a separate abstraction layer.

### Domain events: three cases

| Case | Example | Modeling |
|------|---------|----------|
| State transition | `OrderPlaced`, `OrderPaid` | translator handler derives from the diff ([ADR-011](adr/adr-011-no-business-events-in-storage.md)) |
| Fact without state | `UserLoggedIn`, `EmailSent` | `session.AppendAsync(...)` into the append-only **event log** ([ADR-013](adr/adr-013-business-event-log.md)) |
| Trigger ("do X afterwards") | confirmation email | handler subscription — no stored event |

The event log (`papuma.event`) shares the session transaction, policies,
metamodel and processing engine with the change feed, but has its own checkpoints
and allows type-specific retention. Red line: it is **never a replay source for
state**.

---

## 9. Privacy layer

Policies are applied when the diff is produced — **before** the ChangeRecord is
written:

| Policy        | Diff entry                                    | Use                                  |
|---------------|-----------------------------------------------|--------------------------------------|
| `Track`       | `{ "old": ..., "new": ... }`                  | default                              |
| `Redact`      | `{ "changed": true }`                         | PII nobody needs in the feed         |
| `Reference`   | `{ "ref": "User/123/email" }`                 | value stays exclusively in the document or the sensitive store |
| `Hash`        | `{ "changed": true, "hash": "..." }`          | password hashes, comparability without content |
| `DoNotTrack`  | field does not appear in the diff             | telemetry fields                     |

If a document is GDPR-erased, the values disappear with the document; the feed
then contains only references and `changed` flags — no personal content. The
hybrid model from v1's sensitive-data-reference-pattern ADR (an explicit,
versioned sensitive data store, opt-in resolution in projections) is adopted
conceptually ([ADR-007](adr/adr-007-privacy-policies.md)).

---

## 10. Tenancy

Multi-tenancy stays first-class as in v1: `tenant_id` is part of the primary key
of documents and changes, the `DocumentSession` is always bound to a tenant, and
`Papuma.Kernel.AspNetCore` continues to provide tenant resolution. Tenant ids are
whitelisted (`^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$`, GUIDs fit; `ScopeContext.Tenant(Guid)`
gives the canonical form) and only ever reach SQL as parameters. Application
tables in the same database reuse the row-level security through
`papuma.scope_visible`/`scope_writable` (ADR-019,
[recipe](recipes/same-database-read-models.md)); integration tests run as a
non-superuser role via `Papuma.Kernel.Testing` (ADR-021), so they exercise it.

---

## 11. Deliberately NOT part of Papuma Kernel

- **Provider abstraction / other databases** — Postgres-only, see ADR-001.
- **Event sourcing / an event store** — changes are derived, not the truth; also
  not as an opt-in mode (ADR-023). Stream-shaped aggregates:
  [recipe](recipes/stream-shaped-aggregates.md).
- **Read-model generation, query DSL, LINQ provider** — projections write their
  own SQL.
- **Cross-document transactions beyond the session** — within one session,
  multi-document commits are atomic (section 5, "session = unit of work");
  cross-document *constraints* and distributed sagas are application business.
- **Automatic domain events** — see ADR-011.
- **`ReconstructAtVersionAsync<T>(id, version)`** — read-only time travel that
  reconstructs a document's state at any historical version without persisting
  it. The mechanism exists (`RollbackAsync` already replays diffs backwards,
  §20), but the current API always writes the result as a new update. A
  read-only variant would serve audit UIs and debugging. Candidate for a future
  phase when the use case materialises.
- **Migration code from v1** — the reboot is complete.

---

## 12. ADR index

| ADR | Title | Status |
|-----|-------|--------|
| [001](adr/adr-001-postgresql-18-only.md) | PostgreSQL ≥ 18 as the only target database | Accepted |
| [002](adr/adr-002-document-as-truth.md) | Document as source of truth, change feed derived | Accepted |
| [003](adr/adr-003-write-path-concurrency.md) | Atomic write path with optimistic concurrency and RETURNING OLD/NEW | Accepted |
| [004](adr/adr-004-changerecord-diff-only.md) | ChangeRecord stores a reversible diff, no snapshots | Accepted |
| [005](adr/adr-005-schema-evolution.md) | Schema evolution via schema_version and upcasters | Accepted |
| [006](adr/adr-006-keys-and-constraints.md) | Keys and constraints via metamodel and expression indexes | Accepted; amended by ADR-020 |
| [007](adr/adr-007-privacy-policies.md) | Privacy policies: attributes as defaults, fluent as override | Accepted |
| [008](adr/adr-008-rollback-is-update.md) | Rollback is an update with metadata | Accepted |
| [009](adr/adr-009-projections-as-dumb-handlers.md) | Projections as dumb change handlers | Accepted; ordering amended by ADR-022, extended by ADR-024 |
| [010](adr/adr-010-feed-consumption.md) | Snapshot-based polling with LISTEN/NOTIFY wakeup | Accepted; point 2 superseded by ADR-022 |
| [011](adr/adr-011-no-business-events-in-storage.md) | No domain events in the storage layer | Accepted |
| [012](adr/adr-012-partial-updates.md) | Partial updates as a patch primitive (no load, atomic via jsonb_set + RETURNING) | Accepted |
| [013](adr/adr-013-business-event-log.md) | Domain events: translator, append-only event log and trigger handlers | Accepted |
| [014](adr/adr-014-bulk-operations.md) | Bulk operations as set-based patch (key predicates or id lists, one ChangeRecord per document) | Accepted |
| [015](adr/adr-015-gdpr-tooling.md) | GDPR tooling: export/inventory/redaction in the kernel, legal decisions per tenant in the application | Accepted (implemented: phase 12) |
| [016](adr/adr-016-policy-projected-reads.md) | Policy-projected reads (masked load) + the content MCP boundary; no generic projection MCP | Accepted (implemented) |
| [017](adr/adr-017-actor-id-column.md) | Actor identity as a first-class column (actor_id, created_by, updated_by) | Accepted |
| [018](adr/adr-018-causation-type-metadata.md) | Causation type as a metadata field (command name in JSONB, no schema change) | Accepted |
| [019](adr/adr-019-scope-predicates-for-application-tables.md) | Scope predicates for application tables (papuma.scope_visible / scope_writable as the RLS contract) | Accepted |
| [020](adr/adr-020-composite-keys.md) | Composite keys (uniqueness and lookup over several fields, one multi-expression index) | Accepted |
| [021](adr/adr-021-testing-package.md) | A narrow, test-framework-agnostic testing package; broader one deferred against objective triggers | Accepted |
| [022](adr/adr-022-snapshot-cursor.md) | Snapshot cursor: the feed follows causal order (PgQ-style slices, lag counted) | Accepted |
| [023](adr/adr-023-no-event-sourcing-mode.md) | No event-sourcing mode; stream-shaped aggregates as an application pattern | Accepted |
| [024](adr/adr-024-projections-and-effect-handlers.md) | Projections are declared (`IProjection`: versioned rebuild, reset hook); effects can start at the head | Accepted |

## 13. Recipes (tutorial precursors)

Application patterns on top of the ADRs, as drafts for later tutorials:

- [Realtime UI notifications on document changes](recipes/realtime-ui-notifications.md)
  — a SignalR notifier as a change handler, incl. the presence boundary.
- [AI consumers of the change feed](recipes/ai-consumers.md)
  — pgvector embeddings, natural-language audit, anomaly detection.
- [Workflows and sagas on kernel primitives](recipes/workflow-saga.md)
  — human-in-the-loop, compensation, the dueAt timer; backed by the runnable
  [sample app](https://github.com/papumabiz/Papuma.Kernel/blob/master/samples/shop-minimal-api/README.md).
- [The NATS bridge](recipes/nats-bridge.md)
  — publishing both feeds to JetStream with seq-based dedup; runnable in the
  sample via `Nats:Url`.
- [External read models](recipes/external-read-models.md)
  — the one projection pattern for search engines, vector stores and caches
  (Manticore, Qdrant, Redis), plus where DotNetCore.CAP does and does not fit.
- [Schema for projection tables](recipes/projection-schema.md)
  — idempotent DDL in an `ISchemaContributor` (after the kernel schema, before the
  feed workers); breaking changes as rebuild via a new handler name.
- [Read models in the same database](recipes/same-database-read-models.md)
  — projection tables under the kernel's row-level security via
  `papuma.scope_visible`/`scope_writable` (ADR-019).
- [Field-level encryption](recipes/field-level-encryption.md)
  — storing values that must be decrypted at another trust boundary (bank
  accounts); ciphertext + Redact, envelope/KMS, and why the kernel stays out.
- [Event Modeling slices](recipes/event-modeling-slices.md)
  — building applications the Dymitruk/Dilger way (command/view/automation/
    translation slices) on a document-sourced kernel; where it's even better and
    where real Event Sourcing is the truer fit.
- [Stream-shaped aggregates](recipes/stream-shaped-aggregates.md)
  — ledgers without event sourcing: document as state, postings as event-log
  facts in the same commit (save before append), reconciled, never replayed
  (ADR-023).
- [Causation tracking](recipes/causation-tracking.md)
  — automatic enrichment of actor, causation type and causation id in ASP.NET
  Core (enricher pattern) and manual setup for CLI applications (ADR-017/018).

The cross-language contract for consuming the feeds directly is specified in
[feed-wire-format.md](feed-wire-format.md), with runnable Python/Go clients in
[samples/polyglot-consumers](https://github.com/papumabiz/Papuma.Kernel/blob/master/samples/polyglot-consumers/README.md).
