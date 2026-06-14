// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.AspNetCore.Dashboard;

/// <summary>
/// Assembles the dashboard's JSON payload from the existing diagnostics surface
/// (phase 11): lag and failures straight from the processors, counter/histogram
/// totals from the in-process <see cref="DashboardMetricsCollector"/>. No own
/// diagnostics logic — the dashboard is a viewer, not a second source of truth.
/// </summary>
internal static class DashboardDataBuilder
{
    public static async Task<JsonObject> BuildAsync(
        ChangeFeedProcessor changeProcessor,
        EventFeedProcessor eventProcessor,
        DashboardMetricsCollector collector,
        CancellationToken ct)
    {
        var json = new JsonObject
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["changeFeed"] = await FeedToJsonAsync(
                () => changeProcessor.GetLagAsync(ct), () => changeProcessor.GetFailuresAsync(ct)),
            ["eventFeed"] = await FeedToJsonAsync(
                () => eventProcessor.GetLagAsync(ct), () => eventProcessor.GetFailuresAsync(ct)),
            ["counters"] = ToJson(collector.CounterTotals),
            ["histograms"] = HistogramsToJson(collector.HistogramTotals),
        };
        return json;
    }

    private static async Task<JsonObject> FeedToJsonAsync(
        Func<Task<IReadOnlyList<ChangeFeedLagSnapshot>>> lag,
        Func<Task<IReadOnlyList<FeedFailure>>> failures)
    {
        var lagJson = new JsonArray();
        foreach (var snapshot in await lag())
        {
            lagJson.Add(new JsonObject
            {
                ["handler"] = snapshot.HandlerName,
                ["checkpoint"] = snapshot.Checkpoint,
                ["latestSeq"] = snapshot.LatestSeq,
                ["lag"] = snapshot.Lag,
            });
        }

        var failureJson = new JsonArray();
        foreach (var failure in await failures())
        {
            failureJson.Add(new JsonObject
            {
                ["handler"] = failure.HandlerName,
                ["seq"] = failure.Seq,
                ["attempts"] = failure.Attempts,
                ["lastError"] = Truncate(failure.LastError, 300),
                ["nextRetryAt"] = failure.NextRetryAt.ToString("O"),
            });
        }

        return new JsonObject { ["lag"] = lagJson, ["failures"] = failureJson };
    }

    private static JsonObject ToJson(IReadOnlyDictionary<string, long> totals)
    {
        var json = new JsonObject();
        foreach (var (series, total) in totals.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            json[series] = total;
        }

        return json;
    }

    private static JsonObject HistogramsToJson(IReadOnlyDictionary<string, (long Count, double Sum)> totals)
    {
        var json = new JsonObject();
        foreach (var (series, (count, sum)) in totals.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            json[series] = new JsonObject
            {
                ["count"] = count,
                ["avgMs"] = count == 0 ? 0 : Math.Round(sum / count, 3),
            };
        }

        return json;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
