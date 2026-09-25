# Papuma Kernel

<p align="center"><img src="https://raw.githubusercontent.com/papumabiz/Papuma.Kernel/master/assets/logo.png" width="300" /></p>

<p align="center">
  <a href="https://github.com/papumabiz/Papuma.Kernel/actions/workflows/ci.yml"><img src="https://github.com/papumabiz/Papuma.Kernel/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
  <img src="https://img.shields.io/badge/PostgreSQL-%E2%89%A518-336791" alt="PostgreSQL 18+" />
  <img src="https://img.shields.io/badge/SQLite-embedded-003B57" alt="SQLite embedded" />
  <img src="https://img.shields.io/badge/version-1.2.1-blue" alt="1.2.1" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT" />
</p>

<p align="center"><strong>Your documents are the truth. The change feed follows automatically.</strong></p>

Papuma Kernel is a small application kernel for .NET 10, built as **two
independent products sharing one model**. You store plain C# objects as JSON
documents; the kernel derives a **reversible change feed in the same atomic
transaction**, applies your **privacy policies before anything is recorded**,
and delivers every change to your handlers strictly in order. You get what
event sourcing promises — a complete, auditable history and reactive
projections — without the replay obligation, the mandatory event modeling, or
the GDPR headache.

This is **document-sourced CQRS**: the document is the source of truth, the feed
is derived from it (never the other way around).

- **`Papuma.Kernel`** — PostgreSQL ≥ 18. One primitive makes the write path
  work: `RETURNING OLD/NEW` gives the before- and after-state in a single
  statement, so there is no outbox to forget and no window in which the diff
  can lie. Multi-tenant, row-level-security-isolated, built for servers.
- **`Papuma.Kernel.Local`** — SQLite, no server. Same documents, same
  reversible diffs, same change feed, same privacy policies — for
  single-writer desktop and mobile apps that have no business running a
  database server. See [the design rationale](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/analyses/local-kernel-sqlite-sibling.md)
  for what's shared and what's deliberately different per engine.

> **The 1.0 reboot** — rebuilt from scratch on PostgreSQL ≥ 18; no migration path
> from 0.x (see [CHANGELOG](https://github.com/papumabiz/Papuma.Kernel/blob/master/CHANGELOG.md)). The marketing one-pager with diagrams
> and measured numbers lives in [docs/factsheet.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/factsheet.md).

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

No server, same model — `Papuma.Kernel.Local` mirrors the same call shape:

```csharp
builder.Services
    .AddPapumaKernelLocal(o =>
    {
        o.DbPath = Path.Combine(appDataDir, "app.db");
        o.Model(m => m
            .Document<User>(d => d.UniqueKey(x => x.Email))
            .Event<UserLoggedIn>(e => e.Retention(TimeSpan.FromDays(90))));
    })
    .AddChangeHandler<UserProjection>();

await using var session = store.OpenSession(ScopeContext.Tenant("local"));
await session.SaveAsync(user, expectedVersion: 0);
await session.CommitAsync();
```

```bash
dotnet build Papuma.Kernel.slnx
dotnet test  Papuma.Kernel.slnx   # Postgres suite needs Docker/Podman; SQLite suite needs nothing extra
```

Learn by building: [docs/tutorial.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/tutorial.md) · terse API tour: [docs/getting-started.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/getting-started.md).

## What ships in the box

Shared between both kernels (same code, `Papuma.Kernel.Core`, not two
implementations pretending to agree):

- **Atomic write primitives** — versioned Save/Delete, single-statement patches
  (`Set`/`Remove`/`Increment`), set-based bulk ops, append-only rollback, and a
  bounded counter that cannot oversell (proven under 12-way concurrency on
  Postgres).
- **Privacy by construction** — field policies (`Redact`/`Hash`/`Reference`/
  `DoNotTrack`) applied *inside* the write transaction, so sensitive values never
  reach the feed, the logs, the traces, or AI consumers. The same policies
  project onto masked reads ([ADR-016](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/adr/adr-016-policy-projected-reads.md)).
- **GDPR tooling** — Art.-30 data inventory from the metamodel, Art.-15/20 subject
  export, history redaction with a mandatory audit trail ([gdpr.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/gdpr.md)).
- **Processing engine** — strict per-handler ordering, persisted checkpoints,
  retry/backoff, poison handling, one-call rebuild.
- **Event log** — first-class facts (`UserLoggedIn`) beside state changes, same
  transaction, same policies, per-type retention.
- **Schema evolution** — lazy upcasting with version guards, same
  `Upcast(fromVersion, …)` registration on either kernel.
- **Declared keys** — unique and lookup keys as partial expression indexes,
  including composite keys over several fields
  (`UniqueKey(x => new { x.ProjectId, x.Number })`, [ADR-020](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/adr/adr-020-composite-keys.md)).

Postgres-only (`Papuma.Kernel`):

- **Two-layer multi-tenancy** — explicit scope predicates **plus** PostgreSQL
  row-level security; fail-closed (a missing scope yields empty reads, never a
  leak). Your own tables in the same database join in through
  `papuma.scope_visible`/`scope_writable` ([ADR-019](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/adr/adr-019-scope-predicates-for-application-tables.md)).
- **Testing that exercises RLS** — `Papuma.Kernel.Testing` runs tests as a
  non-superuser role and drains feeds deterministically ([ADR-021](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/adr/adr-021-testing-package.md)).
- **Multi-instance leader failover** via `FOR UPDATE SKIP LOCKED` — no extra
  infrastructure for concurrent processor instances.
- **AI-ready** — an [MCP server](https://github.com/papumabiz/Papuma.Kernel/tree/master/src/Papuma.Kernel.Mcp) over the diagnostics and
  scope-bound, policy-masked reads (read-only by default).
- **Observability** — BCL `Meter` + `ActivitySource` (zero vendor deps),
  OpenTelemetry-ready, feed-lag health check, and an **embedded live dashboard**
  (`MapPapumaDashboard()`).

SQLite-only (`Papuma.Kernel.Local`):

- **No server, no daemon, no port** — one file, opens in milliseconds, single
  writer enforced by the OS file lock, not application code.
- **In-process feed wakeup** — a `SqliteChangeNotifier` replaces LISTEN/NOTIFY;
  no network round trip, near-instant delivery.
- **WAL journal mode + busy timeout on every connection**, applied through one
  shared factory — a background feed processor writing doesn't block the UI
  reading, verified empirically, not assumed.
- Genuinely simpler where the single-writer topology allows it: no RLS
  machinery, no gapless-read snapshot logic — see the
  [design rationale](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/analyses/local-kernel-sqlite-sibling.md) for exactly
  what's dropped and why that's safe, not a shortcut.

## Packages

| Package | What it is |
|---|---|
| `Papuma.Kernel` | PostgreSQL kernel — store, diff engine, policies, feeds, hosting |
| `Papuma.Kernel.Local` | SQLite kernel — same model, single-writer embedded/desktop use, no server |
| `Papuma.Kernel.AspNetCore` | optional ASP.NET Core integration — tenant middleware, feed-lag health check, embedded dashboard |
| `Papuma.Kernel.Mcp` | optional MCP server — read-only diagnostics + masked content tools for agents |
| `Papuma.Kernel.Testing` | optional integration-test support — PostgreSQL 18 test database with a non-superuser role (RLS applies), feed draining; test-framework agnostic |
| `Papuma.Kernel.FSharp` | optional F# facade — `Result`-returning writes, an `IAsyncDisposable`-safe session runner; works with either kernel ([details](https://github.com/papumabiz/Papuma.Kernel/blob/master/src/Papuma.Kernel.FSharp/README.md)) |

`Papuma.Kernel.Core` (diff engine, policies, model, validation) is shared
internally by the two kernels; it is not independently published — its
assembly ships embedded inside whichever kernel package you install.

## Samples

| Sample | Shows |
|---|---|
| [shop-minimal-api](https://github.com/papumabiz/Papuma.Kernel/tree/master/samples/shop-minimal-api) | the breadth — a mini shop touching every kernel concept: approval workflows with humans in the loop, saga compensation, inventory that cannot oversell, realtime UI push, the MCP endpoint and the dashboard |
| [event-modeled-slices](https://github.com/papumabiz/Papuma.Kernel/tree/master/samples/event-modeled-slices) | the *shape* — one vertical slice of each Event Modeling type (Command/View/Automation) with a pure, infrastructure-free Decider test |
| [polyglot-consumers](https://github.com/papumabiz/Papuma.Kernel/tree/master/samples/polyglot-consumers) | the feed as a cross-language contract — Python (psycopg3) and Go (pgx) consumers, ~50 lines each |
| [fsharp-local-todo](https://github.com/papumabiz/Papuma.Kernel/tree/master/samples/fsharp-local-todo) | `Papuma.Kernel.FSharp` end to end, on `Papuma.Kernel.Local` (SQLite — no server, no Docker) |

The first three samples run against `Papuma.Kernel` (Postgres);
`fsharp-local-todo` is the first sample against `Papuma.Kernel.Local`.

## Recipes

Pattern guides on kernel primitives ([docs/recipes](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/recipes)):
[workflow-saga](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/workflow-saga.md) ·
[realtime-ui-notifications](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/realtime-ui-notifications.md) ·
[same-database-read-models](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/same-database-read-models.md) (RLS on your tables) ·
[external-read-models](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/external-read-models.md) (search/vector/cache) ·
[nats-bridge](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/nats-bridge.md) ·
[field-level-encryption](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/field-level-encryption.md) ·
[event-modeling-slices](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/event-modeling-slices.md) ·
[ai-consumers](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/recipes/ai-consumers.md).

## Documentation map

- **Start:** [tutorial.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/tutorial.md) — build one app end to end (guided) · [getting-started.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/getting-started.md) — the five-minute API tour · marketing one-pager: [factsheet.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/factsheet.md)
- **Architecture:** [architecture.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/architecture.md) · the *why* behind every decision: [concepts.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/concepts.md)
- **Decisions:** [the ADRs](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/adr) — each a single, dated, reversible choice
- **`Papuma.Kernel.Local` (SQLite):** [design rationale and what's different per engine](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/analyses/local-kernel-sqlite-sibling.md) — no dedicated getting-started yet; the write/read API mirrors `Papuma.Kernel`'s (`SaveAsync`/`LoadAsync`/`PatchAsync`/… on `SqliteDocumentSession`, `AddPapumaKernelLocal` for hosting)
- **Cross-language:** the [feed wire format](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/feed-wire-format.md) consumers rely on
- **Agents:** [llms.txt](https://github.com/papumabiz/Papuma.Kernel/blob/master/llms.txt) and `docs/ai/` are shipped inside the NuGet package
- **Recipes:** [pattern guides](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/recipes) on kernel primitives
- Frozen 0.x/v1 material (German, unmaintained) lives under [docs/legacy](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/legacy).

## Maturity, stated plainly

`1.2.1` — the Postgres kernel's design is complete (13 implementation phases,
every decision recorded as an ADR, every identified risk closed with a test or a
measurement; **the full integration suite runs against real PostgreSQL 18 on
every CI build**), but it has **not yet
carried production traffic**. Best fit today: internal line-of-business
systems and new products built by teams that control their PostgreSQL
version. For regulated, mission-critical workloads, run a pilot first — the
observability to judge it is built in.

`Papuma.Kernel.Local` is newer and should be read as such: full parity with
the Postgres kernel's write/read/patch/GDPR/rollback/feed-processing surface,
**58 tests green against the real SQLite engine** (including empirically
verified driver behavior, not assumed — WAL mode, busy timeouts, expression
index matching), but zero hours of real application traffic yet and no
performance benchmarks (only correctness). Good fit today for exactly what it
was built for: local desktop/mobile storage where a server is the wrong tool.
Treat it as earlier-stage than the Postgres kernel until it's proven the same
way.

## What it deliberately is not

- **Not an ORM or query DSL** — lookups run over declared, indexed keys;
  anything richer is a projection or a SQL view (enforced, not just advised).
  True on both kernels.
- **Not event sourcing** — the document is the truth, the feed is derived.
- **Not one database-agnostic abstraction pretending to support everything.**
  `Papuma.Kernel` exploits PostgreSQL ≥ 18 without apology; `Papuma.Kernel.Local`
  is an independent SQLite implementation sharing the model, not a storage
  seam bolted under one codebase — see
  [why that's a different (and deliberate) design](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/analyses/local-kernel-sqlite-sibling.md#1-why-this-doesnt-reopen-adr-001).
- **Not a workflow/BPMN engine** — durable state machines and timers are
  documented patterns on kernel primitives (with running sample code).

## Contributing

Issues and pull requests are welcome — start with [CONTRIBUTING.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/CONTRIBUTING.md);
it covers the build prerequisites (Docker for the PostgreSQL suite), the conventions,
and when a change needs an ADR. Security reports go through
[SECURITY.md](https://github.com/papumabiz/Papuma.Kernel/blob/master/SECURITY.md), never a public issue.

## License

MIT — see [LICENSE](https://github.com/papumabiz/Papuma.Kernel/blob/master/LICENSE).
