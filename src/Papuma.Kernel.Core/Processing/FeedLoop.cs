// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Runtime.ExceptionServices;

namespace Papuma.Kernel.Processing;

/// <summary>
/// A fault of one processing cycle that is not a handler failure (those are recorded in
/// <c>papuma.failure</c>): the database was unreachable, a batch could not be loaded,
/// a checkpoint could not be written.
/// </summary>
/// <param name="Handler">The handler whose batch faulted; <c>null</c> for a fault of the whole cycle.</param>
/// <param name="Error">The exception.</param>
internal readonly record struct FeedFault(string? Handler, Exception Error);

/// <summary>The outcome of one processing cycle over all handlers.</summary>
/// <param name="Processed">Records delivered across all handlers.</param>
/// <param name="Faults">Handlers whose batch faulted this cycle; the other handlers still ran.</param>
internal sealed record FeedCycleResult(int Processed, IReadOnlyList<FeedFault> Faults)
{
    /// <summary>A cycle that faulted as a whole before any handler ran.</summary>
    public static FeedCycleResult Failed(Exception error) => new(0, [new FeedFault(null, error)]);

    /// <summary>Gets a value indicating whether any handler or the cycle itself faulted.</summary>
    public bool HasFaults => Faults.Count > 0;
}

/// <summary>
/// Shared rules of the feed run loops (PostgreSQL and SQLite, change and event feed): how
/// long to wait after consecutive faulted cycles, how often to log them, and how a faulted
/// cycle is reported by the one-shot <c>ProcessOnceAsync</c>.
/// </summary>
internal static class FeedLoop
{
    /// <summary>The ceiling of the wait between faulted cycles.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The wait after <paramref name="consecutiveFaultedCycles"/> faulted cycles in a row:
    /// <c>PollInterval × 2^(n−1)</c>, capped at <see cref="MaxBackoff"/> but never shorter
    /// than the poll interval itself.
    /// </summary>
    /// <param name="pollInterval">The configured poll interval.</param>
    /// <param name="consecutiveFaultedCycles">The number of faulted cycles in a row, at least 1.</param>
    public static TimeSpan BackoffDelay(TimeSpan pollInterval, int consecutiveFaultedCycles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveFaultedCycles, 1);

        // Past 2^20 the cap has long applied; avoids Math.Pow overflow games for huge counts.
        var exponent = Math.Min(consecutiveFaultedCycles - 1, 20);
        var seconds = Math.Min(pollInterval.TotalSeconds * Math.Pow(2, exponent), MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(seconds, pollInterval.TotalSeconds));
    }

    /// <summary>
    /// Whether the <paramref name="consecutiveFaultedCycles"/>-th faulted cycle in a row is
    /// logged at error level: the first, then every power of two — a database outage logs a
    /// handful of lines, not one per cycle.
    /// </summary>
    /// <param name="consecutiveFaultedCycles">The number of faulted cycles in a row, at least 1.</param>
    public static bool ShouldLog(int consecutiveFaultedCycles) =>
        consecutiveFaultedCycles >= 1 && (consecutiveFaultedCycles & (consecutiveFaultedCycles - 1)) == 0;

    /// <summary>
    /// Reports the faults of a one-shot cycle to its caller: a single fault is rethrown as it
    /// was (type and stack preserved), several as an <see cref="AggregateException"/>.
    /// </summary>
    /// <param name="faults">The faults of the cycle.</param>
    public static void ThrowIfFaulted(IReadOnlyList<FeedFault> faults)
    {
        ArgumentNullException.ThrowIfNull(faults);

        if (faults.Count == 1)
        {
            ExceptionDispatchInfo.Capture(faults[0].Error).Throw();
        }

        if (faults.Count > 1)
        {
            throw new AggregateException(
                "More than one handler could not be processed.", faults.Select(f => f.Error));
        }
    }

    /// <summary>
    /// Runs a feed worker as the body of a hosted service. The loop only ends on shutdown; if
    /// it returns without one, the host is stopped (the exception reaches
    /// <c>BackgroundServiceExceptionBehavior</c>) instead of leaving every projection frozen
    /// behind a "successfully completed" service.
    /// </summary>
    /// <param name="feedName">A name for the message, such as "Change feed".</param>
    /// <param name="hasHandlers">Whether the worker has handlers; a worker without them returns at once by design.</param>
    /// <param name="run">The worker's run loop.</param>
    /// <param name="stoppingToken">The host's shutdown token.</param>
    public static async Task RunHostedAsync(
        string feedName, bool hasHandlers, Func<CancellationToken, Task> run, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await run(stoppingToken);

        if (hasHandlers && !stoppingToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"{feedName} processor stopped without a shutdown request. Stopping the host: a silently " +
                "ended worker would leave projections and effects frozen while the process looks healthy.");
        }
    }
}
