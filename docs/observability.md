# Papuma Kernel — Observability Guide

Status: verified against the implemented API (phase 11, 2026-06-12)

> **Mostly PostgreSQL kernel.** `KernelDiagnostics` metrics and traces are shared
> with `Papuma.Kernel.Local`; the dashboard and the feed-lag health check are not —
> see the playbook's [Differences section](ai/papuma-kernel-playbook.md#differences-when-using-papumakernellocal-sqlite-embedded).

The kernel instruments with **BCL primitives** (`System.Diagnostics.Metrics.Meter`
+ `ActivitySource`, both under the name `Papuma.Kernel`) — without vendor
dependencies. OpenTelemetry, Prometheus exporters, `dotnet-counters` or
Application Insights are *consumers* of these sources.

## Wiring (OpenTelemetry example)

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Papuma.Kernel")
        .AddPrometheusExporter())
    .WithTracing(tracing => tracing
        .AddSource("Papuma.Kernel")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

Without OTel: `dotnet-counters monitor --counters Papuma.Kernel -p <pid>` shows
all metrics live.

## Metrics

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `papuma.feed.lag` | ObservableGauge | `papuma.feed`, `papuma.handler` | **The most important number**: committed records not yet delivered, per handler (counted, ADR-022 — an open transaction is not lag until it commits). Freshness ≈ poll interval (cache, refreshed by `GetLagAsync` and the run loop's idle moments). |
| `papuma.feed.processed` | Counter | `papuma.feed`, `papuma.handler` | Delivered records |
| `papuma.feed.failures` | Counter | `papuma.feed`, `papuma.handler` | Handler failures (every attempt counts) |
| `papuma.feed.poisoned` | Counter | `papuma.feed`, `papuma.handler` | Records skipped as poison — **alert candidate** |
| `papuma.feed.handler.duration` | Histogram (ms) | `papuma.feed`, `papuma.handler` | Duration per handler invocation — exposes the "slow handler" (concepts §14, limit 1) |
| `papuma.feed.cycle.duration` | Histogram (ms) | `papuma.feed` | Duration of one cycle across all handlers |
| `papuma.session.commits` | Counter | — | Committed sessions |
| `papuma.session.commit.duration` | Histogram (ms) | — | Commit duration (incl. NOTIFY) |
| `papuma.session.writes` | Counter | `papuma.operation`, `papuma.document_type` | Written ChangeRecords |
| `papuma.session.events` | Counter | `papuma.event_type` | Appended events |
| `papuma.session.conflicts` | Counter | `papuma.kind` (`concurrency` \| `unique_key`), `papuma.document_type` | Conflicts — high rates indicate hot documents (concepts §4) |

**Dashboard recommendation**: lag per handler (gauge, alert on sustained growth),
poison counter (alert at > 0), conflict rate per document type, p95 of handler
duration. The `AddPapumaChangeFeedLag(...)` health check remains the simplest
entry point.

## Tracing

| Span | Tags | When |
|---|---|---|
| `papuma.session.save` / `.patch` / `.delete` / `.rollback` / `.append` | `papuma.document_type`, `papuma.document_id`, `papuma.tenant`, `papuma.version` | Per write, as a child of the active span (e.g. the ASP.NET Core request) |
| `papuma.feed.handle` | `papuma.feed`, `papuma.handler`, `papuma.seq` | Per handler invocation |

**Trace propagation through the feed**: if an `Activity` is active during a
write, the session writes the `traceparent` into the change/event metadata. The
handler span links to it (a span **link**, not a parent — feed processing is
asynchronous batch work). In the trace viewer this answers *"which request
triggered this projection?"* with one click.

## Diagnostics APIs

```csharp
// History of a document (ADR-003: conflict UIs, audit) — policy-applied
IReadOnlyList<ChangeRecord> history =
    await session.GetHistoryAsync<User>(id, fromVersion: expectedVersion + 1);

// The failure table as an API (both processors)
IReadOnlyList<FeedFailure> failures = await processor.GetFailuresAsync();

// Manual retry after fixing the cause (removes the poison entry)
await processor.RetryFailureAsync(handlerName, seq);
// If the checkpoint already passed the poison seq, additionally
// ResetCheckpointAsync(handlerName) for a replay.
```

Privacy note: spans tag document *ids* and the tenant, never contents; diffs in
`GetHistoryAsync` are policy-applied — sensitive values do not reach the
observability pipeline either (ADR-007).

## The embedded dashboard: a first look without infrastructure

Before Prometheus/Grafana exist (and for a quick glance afterwards), the
`Papuma.Kernel.AspNetCore` package ships a Hangfire-style embedded dashboard —
one self-contained HTML page (no framework, no CDN, no build step) over the
diagnostics APIs:

```csharp
builder.Services.AddPapumaDashboard();      // starts the in-process meter listener
…
app.MapPapumaDashboard("/papuma");          // page + JSON at /papuma/data
```

It shows: throughput cards (writes/commits/events/conflicts/deliveries per
second, computed client-side from cumulative counters between 2-second polls),
lag per handler for both feeds (bars, fed live by the processors), the failure
table (retry pending / poison), and average handler durations. The JSON endpoint
(`{path}/data`) is also a ready-made API for your own UI (e.g. a Blazor/Radzen
page) if you outgrow the built-in page.

Security: the dashboard exposes operational metadata (handler names, lag, error
messages). Protect it like a health endpoint —
`MapPapumaDashboard().RequireAuthorization(...)` or bind it internally.

It is a viewer, not a second source of truth: lag and failures come from the
same `GetLagAsync`/`GetFailuresAsync` the MCP server uses; counter totals are
process-local (they reset with the process — rates are what matters here).

## MCP server (phase 13): the diagnostics APIs for AI agents

The **`Papuma.Kernel.Mcp`** package exposes exactly this diagnostics surface as
MCP tools — a thin wrapper, no own diagnostics logic, read-only by default:

```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport()        // or WithStdioServerTransport()
    .WithPapumaKernel();        // read-only; mutations opt-in:
    // .WithPapumaKernel(o => o with { AllowMutations = true });
```

Over HTTP the server is **stateless** (the default since the MCP C# SDK 2.0): every
request stands alone, no `Mcp-Session-Id` is issued, and a request carrying one is
rejected with 400. The Papuma tools keep no session state — scope comes as a tool
argument — so nothing is lost. A client that insists on sessions needs
`.WithHttpTransport(o => o.Stateless = false)`.

| Tool | Corresponds to | Mutating? |
|---|---|---|
| `get_model_inventory` | `DataInventory.Build(model)` (Art.-30 inventory, policies, keys) | no |
| `get_feed_lag` | `GetLagAsync()` of both processors | no |
| `get_feed_failures` | `GetFailuresAsync()` of both processors | no |
| `get_document_history` | `GetHistoryAsync` (scope-bound, policy-applied) | no |
| `get_document` | `LoadMaskedAsync` by id (ADR-016: policy-masked, scope-bound, `ExposeToMcp()` types only) | no |
| `get_changes_by_correlation` | `GetChangesByCorrelationAsync` — every change of one unit of work ("what did this command do?"), policy-applied, scope-bound | no |
| `get_document_by_key` | masked load by a **declared** single-field key (no free-form query) | no |
| `retry_feed_failure` | `RetryFailureAsync` | yes — only with `AllowMutations` |
| `reset_feed_checkpoint` | `ResetCheckpointAsync` (projections only!) | yes — only with `AllowMutations` |

The content tools (`get_document`, `get_document_by_key`) return **policy-masked**
JSON: sensitive fields are masked, hashed or omitted exactly as in the feed
(ADR-016, concepts §24). A type is only readable when the model opts in with
`d.ExposeToMcp()` — the safe default is "not exposed." There is deliberately no
`gdpr_export` and no generic projection-query tool.

The server runs **inside the application** (the metamodel only comes into being
at app startup from CLR types + fluent config — an external process does not know
it). Deliberately no `gdpr_export` tool: a data-subject export is an application
workflow with delivery decisions, not an agent capability.

## Exposing the dashboard and MCP safely

Both `/papuma` and `/mcp` expose operational metadata (handler names, lag, error
messages) and, for the content tools, masked document data. They must not sit on
the public surface unguarded. The library never opens a port itself — that is
host territory (concepts §25) — but `MapPapumaDashboard()` and `MapMcp()` return
`IEndpointConventionBuilder`, so you pin them with the standard ASP.NET means.
Three layers, from most robust to most convenient:

1. **Network level (most robust, infrastructure).** A reverse proxy / ingress /
   k8s NetworkPolicy blocks `/papuma` and `/mcp` from outside. Does not depend on
   app code; the right answer for most production deployments.
2. **A management port** — a second Kestrel listener for internal endpoints, the
   ASP.NET equivalent of Spring Boot Actuator's management port:

   ```csharp
   builder.WebHost.ConfigureKestrel(k =>
   {
       k.ListenAnyIP(8080);        // public API + health (LB probes)
       k.ListenLocalhost(9090);    // internal only
   });
   …
   app.MapPapumaDashboard("/papuma").RequireHost("*:9090");
   app.MapMcp("/mcp").RequireHost("*:9090");
   ```

   `RequireHost` is a built-in endpoint convention; on the public port these
   endpoints then return 404. The sample shows this pattern, activated by a
   `ManagementPort` setting.
3. **Authorization.** `.RequireAuthorization(...)` on either builder. The
   dashboard additionally logs a startup warning when mapped without it.

Health (`/health`) is usually kept public for load-balancer probes — it carries
no detail beyond healthy/unhealthy and the lag summary.
