// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Papuma.Kernel.AspNetCore.Dashboard;

/// <summary>
/// In-process aggregation of the kernel's <c>Papuma.Kernel</c> meter for the embedded
/// dashboard: cumulative counter totals (the dashboard page computes rates client-side
/// between polls) and count/sum per histogram (for averages). The observable lag gauge
/// is deliberately not read here — the data endpoint asks the processors directly for
/// fresher values.
/// </summary>
public sealed class DashboardMetricsCollector : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Count, double Sum)> _histograms = new(StringComparer.Ordinal);

    /// <summary>Starts listening to the kernel meter immediately.</summary>
    public DashboardMetricsCollector()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Papuma.Kernel" && instrument is not ObservableInstrument<long>)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument is Counter<long>)
            {
                _counters.AddOrUpdate(SeriesKey(instrument.Name, tags), value, (_, total) => total + value);
            }
        });
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument is Histogram<double>)
            {
                _histograms.AddOrUpdate(SeriesKey(instrument.Name, tags), (1, value),
                    (_, h) => (h.Count + 1, h.Sum + value));
            }
        });
        _listener.Start();
    }

    /// <summary>Gets a snapshot of all cumulative counter series (key: instrument + tags).</summary>
    public IReadOnlyDictionary<string, long> CounterTotals => new Dictionary<string, long>(_counters);

    /// <summary>Gets a snapshot of all histogram series as (count, sum) pairs.</summary>
    public IReadOnlyDictionary<string, (long Count, double Sum)> HistogramTotals =>
        new Dictionary<string, (long, double)>(_histograms);

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private static string SeriesKey(string instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return instrument;
        }

        // Stable, readable series key: instrument|tag=value|tag=value (tag order as emitted —
        // the kernel emits tags in a fixed order per call site).
        var key = instrument;
        foreach (var tag in tags)
        {
            key += $"|{tag.Key}={tag.Value}";
        }

        return key;
    }
}
