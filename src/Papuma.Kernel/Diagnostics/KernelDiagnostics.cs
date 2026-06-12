// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Papuma.Kernel.Diagnostics;

/// <summary>
/// Central instrumentation sources (phase 11): BCL primitives only — OpenTelemetry,
/// Prometheus exporters or <c>dotnet-counters</c> consume them by subscribing to the
/// <see cref="SourceName"/> meter/activity source. The kernel takes no vendor dependency.
/// </summary>
internal static class KernelDiagnostics
{
    /// <summary>The shared name of the meter and the activity source.</summary>
    public const string SourceName = "Papuma.Kernel";

    /// <summary>Spans for session writes and feed handler invocations.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Counters and histograms (callback-free instruments only — the
    /// per-handler lag gauges live on per-processor meters so they can be disposed).</summary>
    public static readonly Meter Meter = new(SourceName);

    // ── Session ────────────────────────────────────────────────────────────────

    public static readonly Counter<long> SessionCommits = Meter.CreateCounter<long>(
        "papuma.session.commits", unit: "{commit}",
        description: "Committed sessions.");

    public static readonly Histogram<double> CommitDuration = Meter.CreateHistogram<double>(
        "papuma.session.commit.duration", unit: "ms",
        description: "Duration of session commits (including NOTIFY).");

    public static readonly Counter<long> Writes = Meter.CreateCounter<long>(
        "papuma.session.writes", unit: "{write}",
        description: "Change records written, tagged by operation and document type.");

    public static readonly Counter<long> EventsAppended = Meter.CreateCounter<long>(
        "papuma.session.events", unit: "{event}",
        description: "Events appended to the event log, tagged by event type.");

    public static readonly Counter<long> Conflicts = Meter.CreateCounter<long>(
        "papuma.session.conflicts", unit: "{conflict}",
        description: "Optimistic concurrency conflicts and unique key violations, tagged by kind.");

    // ── Feed engines ───────────────────────────────────────────────────────────

    public static readonly Counter<long> FeedProcessed = Meter.CreateCounter<long>(
        "papuma.feed.processed", unit: "{record}",
        description: "Records delivered to handlers, tagged by feed and handler.");

    public static readonly Counter<long> FeedFailures = Meter.CreateCounter<long>(
        "papuma.feed.failures", unit: "{failure}",
        description: "Handler failures (each retry attempt counts), tagged by feed and handler.");

    public static readonly Counter<long> FeedPoisoned = Meter.CreateCounter<long>(
        "papuma.feed.poisoned", unit: "{record}",
        description: "Records skipped as poison after exhausting retries, tagged by feed and handler.");

    public static readonly Histogram<double> HandlerDuration = Meter.CreateHistogram<double>(
        "papuma.feed.handler.duration", unit: "ms",
        description: "Duration of a single handler invocation, tagged by feed and handler.");

    public static readonly Histogram<double> CycleDuration = Meter.CreateHistogram<double>(
        "papuma.feed.cycle.duration", unit: "ms",
        description: "Duration of one processing cycle over all handlers, tagged by feed.");
}
