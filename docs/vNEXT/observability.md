# Papuma vNEXT — Observability-Guide

Status: Verifiziert gegen die implementierte API (Phase 11, 2026-06-12)

Der Kernel instrumentiert mit **BCL-Primitives** (`System.Diagnostics.Metrics.Meter`
+ `ActivitySource`, beides unter dem Namen `Papuma.Kernel`) — ohne Vendor-Abhängigkeit.
OpenTelemetry, Prometheus-Exporter, `dotnet-counters` oder Application Insights sind
*Konsumenten* dieser Quellen.

## Verdrahtung (OpenTelemetry-Beispiel)

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

Ohne OTel: `dotnet-counters monitor --counters Papuma.Kernel -p <pid>` zeigt alle
Metriken live.

## Metriken

| Instrument | Typ | Tags | Bedeutung |
|---|---|---|---|
| `papuma.feed.lag` | ObservableGauge | `papuma.feed`, `papuma.handler` | **Die wichtigste Zahl**: stabil-sichtbarer Feed-Kopf minus Checkpoint, pro Handler. Frische ≈ Poll-Intervall (Cache, aktualisiert von `GetLagAsync` und den Idle-Momenten der Run-Loop). |
| `papuma.feed.processed` | Counter | `papuma.feed`, `papuma.handler` | Zugestellte Records |
| `papuma.feed.failures` | Counter | `papuma.feed`, `papuma.handler` | Handler-Fehlschläge (jeder Versuch zählt) |
| `papuma.feed.poisoned` | Counter | `papuma.feed`, `papuma.handler` | Als Poison übersprungene Records — **Alarmkandidat** |
| `papuma.feed.handler.duration` | Histogram (ms) | `papuma.feed`, `papuma.handler` | Dauer pro Handler-Invocation — entlarvt den "langsamen Handler" (concepts §14, Grenze 1) |
| `papuma.feed.cycle.duration` | Histogram (ms) | `papuma.feed` | Dauer eines Zyklus über alle Handler |
| `papuma.session.commits` | Counter | — | Committete Sessions |
| `papuma.session.commit.duration` | Histogram (ms) | — | Commit-Dauer (inkl. NOTIFY) |
| `papuma.session.writes` | Counter | `papuma.operation`, `papuma.document_type` | Geschriebene ChangeRecords |
| `papuma.session.events` | Counter | `papuma.event_type` | Appendete Events |
| `papuma.session.conflicts` | Counter | `papuma.kind` (`concurrency` \| `unique_key`), `papuma.document_type` | Konflikte — hohe Raten deuten auf Hot Documents (concepts §4) |

**Dashboard-Empfehlung**: Lag pro Handler (Gauge, Alarm bei anhaltendem Wachstum),
Poison-Counter (Alarm bei > 0), Konfliktrate pro Dokumenttyp, p95 der Handler-Dauer.
Der `AddPapumaChangeFeedLag(...)`-Health-Check bleibt der einfachste Einstieg.

## Tracing

| Span | Tags | Wann |
|---|---|---|
| `papuma.session.save` / `.patch` / `.delete` / `.rollback` / `.append` | `papuma.document_type`, `papuma.document_id`, `papuma.tenant`, `papuma.version` | Pro Write, als Kind des aktiven Spans (z. B. des ASP.NET-Core-Requests) |
| `papuma.feed.handle` | `papuma.feed`, `papuma.handler`, `papuma.seq` | Pro Handler-Invocation |

**Trace-Propagation durch den Feed**: Ist beim Write eine `Activity` aktiv, schreibt
die Session den `traceparent` in die Change-/Event-Metadata. Der Handler-Span
verlinkt darauf (Span-**Link**, nicht Parent — Feed-Verarbeitung ist asynchrone
Batch-Arbeit). Im Trace-Viewer beantwortet das die Frage *"welcher Request hat diese
Projektion ausgelöst?"* mit einem Klick.

## Diagnose-APIs

```csharp
// Historie eines Dokuments (ADR-003: Konflikt-UIs, Audit) — policy-bereinigt
IReadOnlyList<ChangeRecord> history =
    await session.GetHistoryAsync<User>(id, fromVersion: expectedVersion + 1);

// Failure-Tabelle als API (beide Prozessoren)
IReadOnlyList<FeedFailure> failures = await processor.GetFailuresAsync();

// Manueller Retry nach behobener Ursache (Poison-Eintrag löschen)
await processor.RetryFailureAsync(handlerName, seq);
// Liegt der Checkpoint schon hinter der Poison-seq: zusätzlich
// ResetCheckpointAsync(handlerName) für ein Replay.
```

Hinweis Datenschutz: Spans taggen Dokument-*IDs* und Tenant, nie Inhalte; Diffs in
`GetHistoryAsync` sind policy-bereinigt — sensible Werte erreichen auch die
Observability-Pipeline nicht (ADR-007).
