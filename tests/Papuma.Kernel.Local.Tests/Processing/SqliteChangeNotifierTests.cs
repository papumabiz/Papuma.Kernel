// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// The in-process wakeup: a signal reaches every subscriber (the change and the event
/// processor share one notifier), and a signal sent while a subscriber is busy is kept.
/// </summary>
public sealed class SqliteChangeNotifierTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task OneSignal_WakesEverySubscriber()
    {
        var notifier = new SqliteChangeNotifier();
        using var changeFeed = notifier.Subscribe();
        using var eventFeed = notifier.Subscribe();

        var waits = new[] { changeFeed.WaitAsync(Long, CancellationToken.None), eventFeed.WaitAsync(Long, CancellationToken.None) };
        notifier.TrySignal();

        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SignalWhileBusy_IsKept_ForTheNextWait()
    {
        var notifier = new SqliteChangeNotifier();
        using var subscription = notifier.Subscribe();

        notifier.TrySignal(); // arrives while the subscriber is mid-cycle, not waiting
        notifier.TrySignal(); // coalesced

        await subscription.WaitAsync(Long, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var second = subscription.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await second; // no second pending signal: returns on the short timeout
    }

    [Fact]
    public async Task DisposedSubscription_NoLongerReceives()
    {
        var notifier = new SqliteChangeNotifier();
        var subscription = notifier.Subscribe();
        subscription.Dispose();

        notifier.TrySignal();

        using var other = notifier.Subscribe();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await other.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150)); // signal predates it
    }
}
