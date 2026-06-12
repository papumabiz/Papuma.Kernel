// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Diagnostics;

/// <summary>
/// Integration tests for phase 11: metrics (MeterListener), trace propagation through
/// the feed (ActivityListener), history read API, failure inspection and retry.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ObservabilityTests : IAsyncLifetime
{
    private const string SourceName = "Papuma.Kernel";

    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public ObservabilityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record ObsDoc(string Id, string Name, int Counter = 0);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<ObsDoc>().Build();
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private sealed class RecordingHandler(string name) : IChangeHandler
    {
        public ConcurrentQueue<ChangeRecord> Received { get; } = new();

        public HashSet<string> FailingDocumentIds { get; } = [];

        public string Name => name;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (FailingDocumentIds.Contains(change.DocumentId))
            {
                throw new InvalidOperationException($"Simulated failure for {change.DocumentId}.");
            }

            Received.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    /// <summary>Collects measurements of selected Papuma.Kernel instruments.</summary>
    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ConcurrentBag<(string Instrument, long Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

        public MetricCollector(params string[] instrumentNames)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SourceName && instrumentNames.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var tagMap = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    tagMap[tag.Key] = tag.Value;
                }

                Measurements.Add((instrument.Name, value, tagMap));
            });
            _listener.Start();
        }

        public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

        public long Sum(string instrument, string tagKey, string tagValue) =>
            Measurements
                .Where(m => m.Instrument == instrument
                            && m.Tags.TryGetValue(tagKey, out var v) && (string?)v == tagValue)
                .Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }

    // ── Metrics ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LagGauge_ReportsPerHandler_AndDrainsAfterProcessing()
    {
        using var collector = new MetricCollector("papuma.feed.lag");
        var handler = new RecordingHandler($"h_{Guid.NewGuid():N}");
        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [handler]);
        await processor.ProcessOnceAsync(); // register + drain pre-existing feed

        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new ObsDoc(NewId(), "One"), 0);
            await session.SaveAsync(new ObsDoc(NewId(), "Two"), 0);
            await session.CommitAsync();
        }

        await processor.GetLagAsync(); // refreshes the gauge cache
        collector.RecordObservableInstruments();
        Assert.Equal(2, collector.Sum("papuma.feed.lag", "papuma.handler", handler.Name));

        await processor.ProcessOnceAsync();
        await processor.GetLagAsync();
        collector.Measurements.Clear();
        collector.RecordObservableInstruments();
        Assert.Equal(0, collector.Sum("papuma.feed.lag", "papuma.handler", handler.Name));
    }

    [Fact]
    public async Task PoisonAndFailureCounters_AreRecorded()
    {
        using var collector = new MetricCollector("papuma.feed.failures", "papuma.feed.poisoned", "papuma.feed.processed");
        var handler = new RecordingHandler($"h_{Guid.NewGuid():N}");
        var poisonId = NewId();
        handler.FailingDocumentIds.Add(poisonId);

        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [handler], new ChangeFeedProcessorOptions
        {
            MaxAttempts = 2,
            BaseRetryDelay = TimeSpan.Zero,
        });

        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new ObsDoc(poisonId, "Poison"), 0);
            await session.CommitAsync();
        }

        for (var i = 0; i < 3; i++)
        {
            await processor.ProcessOnceAsync();
        }

        Assert.Equal(2, collector.Sum("papuma.feed.failures", "papuma.handler", handler.Name));
        Assert.Equal(1, collector.Sum("papuma.feed.poisoned", "papuma.handler", handler.Name));
    }

    // ── Tracing ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandlerSpan_LinksToTheOriginatingWriteTrace()
    {
        var captured = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        // Simulated request root (e.g. ASP.NET Core request activity).
        using var root = new Activity("test-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var id = NewId();
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new ObsDoc(id, "Traced"), 0);
            await session.CommitAsync();
        }

        root.Stop();

        // The write span is a child of the request → same trace id.
        var writeSpan = Assert.Single(captured, a =>
            a.OperationName == "papuma.session.save" && (string?)a.GetTagItem("papuma.document_id") == id);
        Assert.Equal(root.TraceId, writeSpan.TraceId);
        Assert.Equal("ObsDoc", writeSpan.GetTagItem("papuma.document_type"));

        // The handler span (asynchronous batch work) links back to the same trace.
        var handler = new RecordingHandler($"h_{Guid.NewGuid():N}");
        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [handler]);
        await processor.ProcessOnceAsync();

        var handlerSpan = Assert.Single(captured, a =>
            a.OperationName == "papuma.feed.handle" && (string?)a.GetTagItem("papuma.handler") == handler.Name
            && a.Links.Any());
        Assert.Equal(root.TraceId, handlerSpan.Links.Single().Context.TraceId);
    }

    // ── History read API (ADR-003 gap) ─────────────────────────────────────────

    [Fact]
    public async Task GetHistory_DeliversTheDiffsOfAConflictWindow()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new ObsDoc(id, "v1"), 0);
        await session.SaveAsync(new ObsDoc(id, "v2"), 1);
        await session.SaveAsync(new ObsDoc(id, "v3"), 2);

        // ADR-003 scenario: a writer holding version 1 conflicts against version 3 and
        // wants to show what changed in between (versions 2..3).
        var window = await session.GetHistoryAsync<ObsDoc>(id, fromVersion: 2);

        Assert.Equal(2, window.Count);
        Assert.Equal([2L, 3L], window.Select(c => c.Version));
        Assert.Equal("v1", (string?)window[0].Diff.Entries["name"].Old);
        Assert.Equal("v2", (string?)window[0].Diff.Entries["name"].New);
        Assert.True(window[1].FieldChanged<ObsDoc>(x => x.Name));
        Assert.Equal(session.CorrelationId.ToString("N"), (string?)window[0].Metadata["correlationId"]);

        var full = await session.GetHistoryAsync<ObsDoc>(id);
        Assert.Equal(3, full.Count);
        Assert.Equal(ChangeOperation.Insert, full[0].Operation);
    }

    // ── Failure inspection + retry ─────────────────────────────────────────────

    [Fact]
    public async Task FailureInspection_And_ManualRetry_Flow()
    {
        var handler = new RecordingHandler($"h_{Guid.NewGuid():N}");
        var id = NewId();
        handler.FailingDocumentIds.Add(id);

        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [handler], new ChangeFeedProcessorOptions
        {
            MaxAttempts = 1, // first failure is already poison
            BaseRetryDelay = TimeSpan.Zero,
        });

        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new ObsDoc(id, "Broken"), 0);
            await session.CommitAsync();
        }

        await processor.ProcessOnceAsync(); // fail → poison entry
        await processor.ProcessOnceAsync(); // skip as poison

        var failure = Assert.Single(await processor.GetFailuresAsync());
        Assert.Equal(handler.Name, failure.HandlerName);
        Assert.Equal(1, failure.Attempts);
        Assert.Contains("Simulated failure", failure.LastError);

        // Fix the cause, then manually retry: checkpoint is already past the poison seq,
        // so a rebuild replays it — RetryFailureAsync clears the block.
        handler.FailingDocumentIds.Clear();
        Assert.True(await processor.RetryFailureAsync(handler.Name, failure.Seq));
        await processor.ResetCheckpointAsync(handler.Name);
        await processor.ProcessOnceAsync();

        Assert.Contains(handler.Received, c => c.DocumentId == id);
        Assert.Empty(await processor.GetFailuresAsync());
    }
}
