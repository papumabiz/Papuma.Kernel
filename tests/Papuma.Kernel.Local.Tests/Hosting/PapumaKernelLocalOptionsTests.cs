// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Hosting;

namespace Papuma.Kernel.Local.Tests.Hosting;

/// <summary>
/// <see cref="PapumaKernelLocalOptions"/> validation (review P-11): bad values fail at
/// startup, not later in a connection attempt or a <see cref="PeriodicTimer"/>.
/// </summary>
public sealed class PapumaKernelLocalOptionsTests
{
    // PeriodicTimer accepts at most 0xFFFFFFFE milliseconds (~49.7 days).
    private static readonly TimeSpan MaxTimerPeriod = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    private sealed record OptionsDoc(string Id);

    private static PapumaKernelLocalOptions Valid()
    {
        var options = new PapumaKernelLocalOptions { ConnectionString = "Data Source=:memory:" };
        options.Model(m => m.Document<OptionsDoc>());
        return options;
    }

    [Fact]
    public void Validate_AcceptsAConnectionStringAndTheDefaults()
    {
        Valid().Validate();
    }

    [Fact]
    public void Validate_AcceptsADbPath()
    {
        var options = Valid();
        options.ConnectionString = null;
        options.DbPath = "app.db";

        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Validate_RejectsABlankConnectionString(string connectionString)
    {
        var options = Valid();
        options.ConnectionString = connectionString;

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(PapumaKernelLocalOptions.ConnectionString), ex.ParamName);
    }

    [Fact]
    public void Validate_AcceptsRetentionIntervalsUpToTheTimerLimit()
    {
        var options = Valid();

        options.RetentionInterval = TimeSpan.FromMilliseconds(1);
        options.Validate();
        options.RetentionInterval = MaxTimerPeriod;
        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsANonPositiveRetentionInterval(int seconds)
    {
        var options = Valid();
        options.RetentionInterval = TimeSpan.FromSeconds(seconds);

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsARetentionIntervalBeyondWhatThePeriodicTimerAccepts()
    {
        var options = Valid();
        options.RetentionInterval = MaxTimerPeriod + TimeSpan.FromMilliseconds(1);

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("RetentionInterval", ex.Message);
    }
}
