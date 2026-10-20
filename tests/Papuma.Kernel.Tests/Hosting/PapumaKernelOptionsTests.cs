// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Papuma.Kernel.Hosting;

namespace Papuma.Kernel.Tests.Hosting;

/// <summary>
/// <see cref="PapumaKernelOptions"/> validation (review P-11): bad values fail at startup,
/// not later in a connection attempt or a <see cref="PeriodicTimer"/>.
/// </summary>
public sealed class PapumaKernelOptionsTests
{
    // PeriodicTimer accepts at most 0xFFFFFFFE milliseconds (~49.7 days).
    private static readonly TimeSpan MaxTimerPeriod = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    private sealed record OptionsDoc(string Id);

    private static PapumaKernelOptions Valid()
    {
        var options = new PapumaKernelOptions { ConnectionString = "Host=localhost;Database=x" };
        options.Model(m => m.Document<OptionsDoc>());
        return options;
    }

    [Fact]
    public void Validate_AcceptsAConnectionStringAndTheDefaults()
    {
        Valid().Validate();
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
        Assert.Equal(nameof(PapumaKernelOptions.ConnectionString), ex.ParamName);
    }

    [Fact]
    public void Validate_StillRequiresExactlyOneOfConnectionStringAndDataSource()
    {
        var options = Valid();
        options.ConnectionString = null;

        Assert.Throws<InvalidOperationException>(options.Validate);
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

    // Review P-12 turned out not to apply: AddHostedService uses TryAddEnumerable, so a second
    // AddPapumaKernel adds no second worker (and no second LISTEN connection). Pinned here.
    [Fact]
    public void AddPapumaKernel_CalledTwice_RegistersEachHostedServiceOnce()
    {
        var services = new ServiceCollection();
        void Configure(PapumaKernelOptions o)
        {
            o.ConnectionString = "Host=localhost;Database=x";
            o.Model(m => m.Document<OptionsDoc>());
        }

        services.AddPapumaKernel(Configure);
        services.AddPapumaKernel(Configure);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
        Assert.Equal(hosted.Distinct().Count(), hosted.Count);
        Assert.Contains(typeof(ChangeFeedHostedService), hosted);
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
