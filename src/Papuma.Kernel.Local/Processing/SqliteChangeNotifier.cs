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
public sealed class SqliteChangeNotifier
{
    // Capacity 1, drop-on-full: a signal means "wake up and re-poll", not a queue of
    // individual events — coalescing extra writes into a dropped write is correct and
    // avoids unbounded buildup between poll cycles.
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false,
    });

    /// <summary>
    /// Signals that new data probably exists. Called from a session's <c>CommitAsync</c>
    /// when the commit had writes.
    /// </summary>
    public void TrySignal() => _channel.Writer.TryWrite(0);

    /// <summary>
    /// Waits for a signal or <paramref name="timeout"/>, whichever comes first — mirrors
    /// the "NOTIFY wakes early, poll interval is the fallback" semantics of the Postgres
    /// kernel's <c>NpgsqlConnection.WaitAsync</c> usage.
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked token fired from the timeout, not from the caller's cancellation
            // — this is the "poll interval elapsed" path, not an error.
        }
    }
}
