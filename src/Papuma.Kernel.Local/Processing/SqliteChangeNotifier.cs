// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Threading.Channels;

namespace Papuma.Kernel.Processing;

/// <summary>
/// In-process wakeup signal replacing Postgres's LISTEN/NOTIFY (ADR-010) for a
/// single-writer embedded store — there is no separate database process to notify, so
/// the write path and the feed processors coordinate directly in memory. Registered as
/// a DI singleton shared between <c>SqliteDocumentStore</c> (signals after a commit with
/// writes) and the feed processors (wait on it instead of blind-polling).
/// </summary>
/// <remarks>
/// A signal is a broadcast: every <see cref="Subscribe">subscription</see> receives it.
/// Each subscription buffers at most one pending signal, so a commit that lands while a
/// subscriber is busy wakes it right after — nothing is lost, nothing piles up.
/// </remarks>
public sealed class SqliteChangeNotifier
{
    private readonly object _gate = new();
    private readonly Channel<byte> _legacy = CreateSignalChannel();
    private Channel<byte>[] _subscribers = [];

    /// <summary>
    /// Signals that new data probably exists. Called from a session's <c>CommitAsync</c>
    /// when the commit had writes.
    /// </summary>
    public void TrySignal()
    {
        _legacy.Writer.TryWrite(0);
        foreach (var subscriber in Volatile.Read(ref _subscribers))
        {
            subscriber.Writer.TryWrite(0);
        }
    }

    /// <summary>
    /// Registers a waiter with its own signal buffer. Subscribe before the first
    /// processing cycle, so a commit during that cycle is not missed; dispose to
    /// unsubscribe.
    /// </summary>
    public SqliteChangeSubscription Subscribe()
    {
        var channel = CreateSignalChannel();
        lock (_gate)
        {
            _subscribers = [.. _subscribers, channel];
        }

        return new SqliteChangeSubscription(channel, () =>
        {
            lock (_gate)
            {
                _subscribers = [.. _subscribers.Where(c => c != channel)];
            }
        });
    }

    /// <summary>
    /// Waits for a signal or <paramref name="timeout"/>, whichever comes first.
    /// </summary>
    [Obsolete("All callers of this method share one buffered signal, so concurrent waiters " +
        "steal each other's wakeups. Use Subscribe() and wait on the subscription.")]
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => WaitOnAsync(_legacy, timeout, ct);

    internal static async Task WaitOnAsync(Channel<byte> channel, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked token fired from the timeout, not from the caller's cancellation
            // — this is the "poll interval elapsed" path, not an error.
        }
    }

    // Capacity 1, drop-on-full: a signal means "wake up and re-poll", not a queue of
    // individual events — coalescing extra writes into a dropped write is correct and
    // avoids unbounded buildup between poll cycles.
    private static Channel<byte> CreateSignalChannel() =>
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
}

/// <summary>
/// One waiter's view of <see cref="SqliteChangeNotifier"/>: its own buffered signal.
/// </summary>
public sealed class SqliteChangeSubscription : IDisposable
{
    private readonly Channel<byte> _channel;
    private readonly Action _unsubscribe;
    private int _disposed;

    internal SqliteChangeSubscription(Channel<byte> channel, Action unsubscribe)
    {
        _channel = channel;
        _unsubscribe = unsubscribe;
    }

    /// <summary>
    /// Waits for a signal or <paramref name="timeout"/>, whichever comes first — mirrors
    /// the "NOTIFY wakes early, poll interval is the fallback" semantics of the Postgres
    /// kernel's <c>NpgsqlConnection.WaitAsync</c> usage.
    /// </summary>
    /// <param name="timeout">The poll interval: the longest time to wait without a signal.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        SqliteChangeNotifier.WaitOnAsync(_channel, timeout, ct);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _unsubscribe();
        }
    }
}
