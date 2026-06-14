# Papuma.Kernel

**Your documents are the truth. Everything else follows automatically.**

A PostgreSQL-native application kernel for .NET 10 that gives you what event
sourcing promises — a complete, auditable change history and reactive
projections — without what event sourcing costs: no replay obligation, no
mandatory event modeling, no fight with GDPR. Store plain C# objects as JSON
documents; the kernel derives a reversible change feed in the same transaction,
applies your privacy policies before anything is recorded, and delivers every
change to your handlers exactly in order.

```mermaid
flowchart LR
    A["C# record<br/>(your domain)"] --> B["DocumentSession<br/>(unit of work)"]
    B -->|"one atomic statement<br/>RETURNING OLD/NEW"| C[("PostgreSQL ≥ 18<br/>document + change feed<br/>+ event log")]
    C -->|"gapless, ordered,<br/>checkpointed"| D["Your handlers"]
    D --> E["Search index"]
    D --> F["Read models / cache"]
    D --> G["SignalR / UI push"]
    D --> H["NATS / Kafka /<br/>webhooks"]
    D --> I["Embeddings / AI"]
```

---

## The one guarantee everything builds on

Every write is **one atomic statement**. PostgreSQL 18's `RETURNING OLD/NEW`
delivers the state before and after the change in the same operation — there is
no window in which the diff can lie, no outbox that can be forgotten, no
dual-write problem to patch over.

```mermaid
sequenceDiagram
    participant App
    participant Session
    participant PG as PostgreSQL 18
    App->>Session: SaveAsync(user, expectedVersion: 5)
    Session->>PG: UPDATE … WHERE version = 5<br/>RETURNING old.data, new.data
    PG-->>Session: both states, atomically
    Session->>Session: diff + privacy policies
    Session->>PG: INSERT ChangeRecord (same transaction)
    Session-->>App: SaveResult (version 6, policy-applied diff)
    Note over PG: Commit makes document AND<br/>change visible together — always.
```

From this single primitive, the rest follows: optimistic concurrency as an
invariant (lost updates are impossible, conflicts are typed), a gapless feed
(MVCC snapshots, not heuristics), and a change history that doubles as your
audit log — every transition with actor, timestamp, correlation and a
reversible field diff.

---

## What ships in the box

| | |
|---|---|
| **Write primitives** | Save/Delete with mandatory version check · single-statement patches (`Set`/`Remove`/`Increment`) · set-based bulk operations · append-only rollback · the atomic bounded counter (never oversell — proven under 12-way concurrency) |
| **Multi-tenancy** | Two independent isolation layers: explicit scope predicates in every query **plus** PostgreSQL row-level security. An erasure in tenant A provably cannot touch tenant B. |
| **Privacy by construction** | Field policies (`Redact`/`Hash`/`Reference`/`DoNotTrack`) applied **inside the write transaction** — sensitive values never reach the feed, the logs, the traces, or your AI consumers. |
| **GDPR tooling** | Art.-30 data inventory from the metamodel · Art.-15/20 subject export (one consistent snapshot) · history redaction with mandatory audit trail for the Art.-17 edge cases |
| **Processing engine** | Strict per-handler ordering · persisted checkpoints · retry with backoff · poison handling · rebuild = one call · leader failover via `FOR UPDATE SKIP LOCKED` — no ZooKeeper, no extra infrastructure |
| **Event log** | First-class facts (`UserLoggedIn`) next to state changes, same transaction, same policies, per-type retention |
| **Schema evolution** | Lazy upcasting with version guards — class changes are a tested function, not a migration weekend |
| **Observability** | BCL `Meter` + `ActivitySource` (zero vendor dependencies) · OpenTelemetry-ready · feed-lag health check · trace propagation from request to projection · **embedded live dashboard** (`MapPapumaDashboard()`) for the day before Prometheus exists |
| **AI-ready** | MCP server over the diagnostics APIs **and scope-bound, policy-masked reads** (read-only by default) · agent playbook and docs shipped inside the NuGet package · the policy-minimized feed is safe LLM reading material by construction |

---

## Numbers, not adjectives

Measured on commodity hardware (i7, local PG-18 container; reusable probes in
`benchmarks/`):

| Metric | Measured |
|---|---:|
| Write path (4 parallel sessions, incl. diff + change record) | ~900 saves/s |
| Feed engine ceiling (delivery overhead per change) | ~37 µs |
| Realistic projection handler (1 SQL upsert per change) | ~1,400 changes/s |
| Diff engine, 1,000-field document | ~0.3 ms |
| Integration tests against real PostgreSQL 18 | 167, green |
| Architecture decision records | 16 |

Scaling limits are not hidden — they are documented with the metric that
detects them and the designed escape route for each
(`docs/vNEXT/concepts.md §14`).

---

## Plays well with everything

- **Any language can consume the feed** — it is two documented Postgres tables
  with a stable JSONB wire format; a Python or Go consumer is ~50 lines.
- **Event buses dock behind the feed** — the derived feed *is* a transactional
  outbox; the NATS/JetStream bridge (runnable in the sample) turns at-least-once
  into exactly-once with one line of dedup.
- **SQL stays a first-class citizen** — views over the JSONB store are a
  sanctioned read lens; reporting and BI need no export pipeline.
- **Three runnable samples** carry it from breadth to shape to reach: a
  mini-shop Minimal API exercising every concept (approval workflows with humans
  in the loop, saga compensation, inventory that cannot oversell, realtime UI
  push, the MCP endpoint and the dashboard); a set of event-modeled vertical
  slices (one Command/View/Automation each, with an infrastructure-free Decider
  test); and Python + Go feed consumers proving the cross-language wire format.

---

## What it deliberately is not

Honesty is cheaper than disappointment:

- **Not an ORM, not a query DSL.** Lookups run over declared, indexed keys;
  everything richer is a projection or a SQL view. This boundary is enforced,
  not just recommended.
- **Not event sourcing.** The document is the truth; the feed is derived. If
  you need event-defined state, you need a different product — and we say so.
- **Not database-agnostic.** PostgreSQL ≥ 18, exploited without apology. The
  single-statement write path *is* the product.
- **Not a workflow/BPMN engine.** Durable state machines, human-in-the-loop
  tasks and timers are documented patterns on kernel primitives (with running
  sample code) — the workflow definition stays your code.

## Maturity, stated plainly

`1.0.1` — the design is complete (13 implementation phases, 16 ADRs,
every identified risk closed with tests or measurements), but it has **not yet
carried production traffic**. Best fit today: internal line-of-business
systems and new products built by teams that control their PostgreSQL version.
For regulated, mission-critical workloads: run a pilot first — the
observability to judge it is built in.

---

## Sixty seconds to running

```csharp
builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = config.GetConnectionString("papuma");
        o.Model(m => m
            .Document<User>(d => d.UniqueKey(x => x.Email))
            .Event<UserLoggedIn>(e => e.Retention(TimeSpan.FromDays(90))));
    })
    .AddChangeHandler<UserProjection>();

// Write — atomic, versioned, policy-applied, feed included:
await using var session = store.OpenSession(ScopeContext.Tenant("acme"));
await session.SaveAsync(user, expectedVersion: 0);
await session.AppendAsync(new UserLoggedIn(user.Id, "web"));
await session.CommitAsync();
```

Schema, indexes and row-level security are created idempotently at startup.
There is no migration step. There is no step two.

**Packages:** `Papuma.Kernel` · `Papuma.Kernel.AspNetCore` · `Papuma.Kernel.Mcp`
**Docs:** shipped inside the package under `docs/`, and at
[github.com/papumabiz/Papuma.Kernel](https://github.com/papumabiz/Papuma.Kernel)
— start with `docs/vNEXT/getting-started.md`. MIT licensed.
