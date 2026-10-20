// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Core.Tests.Processing;

/// <summary>Validation of <see cref="ChangeFeedProcessorOptions"/> names the property that is wrong (review C-11).</summary>
public sealed class ChangeFeedProcessorOptionsTests
{
    [Fact]
    public void ADefaultInstance_IsValid() => new ChangeFeedProcessorOptions().Validate();

    [Fact]
    public void MaxRetryDelayBelowTheBaseDelay_BlamesMaxRetryDelay()
    {
        var options = new ChangeFeedProcessorOptions
        {
            BaseRetryDelay = TimeSpan.FromSeconds(10),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal(nameof(ChangeFeedProcessorOptions.MaxRetryDelay), ex.ParamName);
    }

    [Fact]
    public void ANegativeBaseDelay_BlamesBaseRetryDelay()
    {
        var options = new ChangeFeedProcessorOptions
        {
            BaseRetryDelay = TimeSpan.FromSeconds(-1),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal(nameof(ChangeFeedProcessorOptions.BaseRetryDelay), ex.ParamName);
    }

    [Fact]
    public void EqualDelays_AreValid() =>
        new ChangeFeedProcessorOptions { BaseRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero }.Validate();
}
