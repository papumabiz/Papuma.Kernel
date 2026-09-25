// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Testing;

/// <summary>
/// Runs feed processors to quiescence in tests — the deterministic replacement for the
/// hosted, NOTIFY-driven loop.
/// </summary>
public static class FeedDrainExtensions
{
    /// <summary>The default cycle limit of <c>DrainAsync</c>.</summary>
    public const int DefaultMaxCycles = 1000;

    /// <summary>
    /// Processes the change feed until a cycle delivers nothing, then fails if any of the
    /// processor's handlers recorded a failure. A throwing handler stops its feed
    /// (stop-the-line) and a cycle simply delivers nothing — without the failure check a
    /// test would pass over the exception.
    /// </summary>
    /// <param name="processor">The processor, typically built on <c>PapumaTestDatabase.AppDataSource</c>.</param>
    /// <param name="maxCycles">Upper bound on cycles, against handlers that keep producing changes.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The number of changes delivered across all handlers.</returns>
    /// <exception cref="FeedDrainException">A handler failed, or the feed did not settle within <paramref name="maxCycles"/>.</exception>
    public static Task<int> DrainAsync(
        this ChangeFeedProcessor processor,
        int maxCycles = DefaultMaxCycles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return DrainAsync(processor.ProcessOnceAsync, processor.GetFailuresAsync, maxCycles, ct);
    }

    /// <summary>
    /// Processes the event feed until a cycle delivers nothing, then fails if any of the
    /// processor's handlers recorded a failure (see the change feed overload).
    /// </summary>
    /// <param name="processor">The processor, typically built on <c>PapumaTestDatabase.AppDataSource</c>.</param>
    /// <param name="maxCycles">Upper bound on cycles, against handlers that keep producing events.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The number of events delivered across all handlers.</returns>
    /// <exception cref="FeedDrainException">A handler failed, or the feed did not settle within <paramref name="maxCycles"/>.</exception>
    public static Task<int> DrainAsync(
        this EventFeedProcessor processor,
        int maxCycles = DefaultMaxCycles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return DrainAsync(processor.ProcessOnceAsync, processor.GetFailuresAsync, maxCycles, ct);
    }

    private static async Task<int> DrainAsync(
        Func<CancellationToken, Task<int>> processOnce,
        Func<CancellationToken, Task<IReadOnlyList<FeedFailure>>> getFailures,
        int maxCycles,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCycles, 1);

        var total = 0;
        for (var cycle = 0; cycle < maxCycles; cycle++)
        {
            var delivered = await processOnce(ct);
            total += delivered;
            if (delivered == 0)
            {
                var failures = await getFailures(ct);
                return failures.Count == 0
                    ? total
                    : throw new FeedDrainException(
                        $"{failures.Count} feed failure(s): " +
                        string.Join("; ", failures.Select(f => $"{f.HandlerName} at seq {f.Seq}: {f.LastError}")),
                        failures);
            }
        }

        throw new FeedDrainException(
            $"The feed did not settle within {maxCycles} cycles — a handler keeps producing new records.", []);
    }
}
