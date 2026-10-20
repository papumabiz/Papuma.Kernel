// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Core.Tests.Processing;

/// <summary>
/// The rules shared by all four feed run loops: backoff after faulted cycles, log
/// throttling, the one-shot fault report and the hosted-service guard.
/// </summary>
public sealed class FeedLoopTests
{
    [Theory]
    [InlineData(1, 50, 50)]
    [InlineData(2, 50, 100)]
    [InlineData(3, 50, 200)]
    [InlineData(4, 50, 400)]
    [InlineData(5, 5000, 30000)] // 5 s × 2^4 = 80 s → capped at 30 s
    [InlineData(1000, 5000, 30000)] // no overflow for huge counts
    [InlineData(3, 60000, 60000)] // never shorter than the poll interval itself
    public void BackoffDelay_DoublesFromThePollInterval_CappedAtThirtySeconds(
        int consecutiveFaultedCycles, int pollMs, int expectedMs)
    {
        var delay = FeedLoop.BackoffDelay(TimeSpan.FromMilliseconds(pollMs), consecutiveFaultedCycles);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
    }

    [Fact]
    public void BackoffDelay_RejectsAZeroCycleCount() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FeedLoop.BackoffDelay(TimeSpan.FromSeconds(1), 0));

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(0, false)]
    public void ShouldLog_FirstThenEveryPowerOfTwo(int consecutiveFaultedCycles, bool expected) =>
        Assert.Equal(expected, FeedLoop.ShouldLog(consecutiveFaultedCycles));

    [Fact]
    public void ThrowIfFaulted_NoFaults_DoesNothing() => FeedLoop.ThrowIfFaulted([]);

    [Fact]
    public void ThrowIfFaulted_OneFault_RethrowsTheOriginalException()
    {
        var original = new InvalidOperationException("boom");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => FeedLoop.ThrowIfFaulted([new FeedFault("h", original)]));

        Assert.Same(original, thrown);
    }

    [Fact]
    public void ThrowIfFaulted_SeveralFaults_Aggregates()
    {
        var a = new InvalidOperationException("a");
        var b = new TimeoutException("b");

        var thrown = Assert.Throws<AggregateException>(
            () => FeedLoop.ThrowIfFaulted([new FeedFault("x", a), new FeedFault("y", b)]));

        Assert.Equal([a, b], thrown.InnerExceptions);
    }

    [Fact]
    public async Task RunHostedAsync_ReturnsNormally_OnShutdown()
    {
        using var cts = new CancellationTokenSource();

        await FeedLoop.RunHostedAsync("Change feed", hasHandlers: true, async ct =>
        {
            await cts.CancelAsync();
            await Task.Yield();
        }, cts.Token);
    }

    [Fact]
    public async Task RunHostedAsync_LoopEndsWithoutShutdown_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FeedLoop.RunHostedAsync("Change feed", hasHandlers: true, _ => Task.CompletedTask, CancellationToken.None));

        Assert.Contains("Change feed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public Task RunHostedAsync_WithoutHandlers_MayReturnAtOnce() =>
        FeedLoop.RunHostedAsync("Change feed", hasHandlers: false, _ => Task.CompletedTask, CancellationToken.None);
}
