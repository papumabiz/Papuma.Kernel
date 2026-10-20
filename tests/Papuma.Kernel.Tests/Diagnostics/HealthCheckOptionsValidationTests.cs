// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Papuma.Kernel.AspNetCore.Processing;

namespace Papuma.Kernel.Tests.Diagnostics;

/// <summary>
/// Review A-9: <see cref="ChangeFeedHealthCheckOptions"/> are validated wherever the values
/// come from (extension argument, <c>Configure</c>, configuration binding) — at host start
/// and, as a floor, on first use — not only in the extension's argument guard.
/// </summary>
public sealed class HealthCheckOptionsValidationTests
{
    [Fact]
    public async Task Host_Starts_WithTheExtensionArgument()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHealthChecks().AddPapumaChangeFeedLag(25);

        using var host = builder.Build();
        await host.StartAsync();

        Assert.Equal(25, host.Services.GetRequiredService<IOptions<ChangeFeedHealthCheckOptions>>().Value.MaxAllowedLag);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task Host_FailsToStart_WhenConfigureOverridesWithAnInvalidLag(long lag)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHealthChecks().AddPapumaChangeFeedLag(25);
        builder.Services.Configure<ChangeFeedHealthCheckOptions>(o => o.MaxAllowedLag = lag);

        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(nameof(ChangeFeedHealthCheckOptions.MaxAllowedLag), ex.Message);
    }

    [Fact]
    public async Task Host_FailsToStart_WhenConfigurationBindsAnInvalidLag()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHealthChecks().AddPapumaChangeFeedLag(25);
        builder.Services.Configure<ChangeFeedHealthCheckOptions>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["MaxAllowedLag"] = "-5" })
                .Build());

        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void FirstUse_ThrowsForAnInvalidLag_EvenWithoutAHost()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddPapumaChangeFeedLag(25);
        services.Configure<ChangeFeedHealthCheckOptions>(o => o.MaxAllowedLag = 0);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ChangeFeedHealthCheckOptions>>().Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(long.MaxValue)]
    public void FirstUse_AcceptsAValidLag(long lag)
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddPapumaChangeFeedLag(25);
        services.Configure<ChangeFeedHealthCheckOptions>(o => o.MaxAllowedLag = lag);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(lag, provider.GetRequiredService<IOptions<ChangeFeedHealthCheckOptions>>().Value.MaxAllowedLag);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Extension_StillRejectsAnInvalidArgumentImmediately(long lag)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => services.AddHealthChecks().AddPapumaChangeFeedLag(lag));

        Assert.Equal("maxAllowedLag", ex.ParamName);
    }
}
