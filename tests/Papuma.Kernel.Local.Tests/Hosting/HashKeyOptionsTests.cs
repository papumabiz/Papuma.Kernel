// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Hosting;

/// <summary>The hosting options that configure the keyed <c>Hash</c> policy (ADR-025) on SQLite.</summary>
public sealed class HashKeyOptionsTests
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private sealed record Account(string Id, string Name);

    private static ServiceProvider Host(Action<PapumaKernelLocalOptions> configure, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddPapumaKernelLocal(o =>
        {
            o.ConnectionString = "Data Source=:memory:";
            o.Model(m => m.Document<Account>());
            o.EnsureSchema = false;
            o.RunFeedWorkers = false;
            configure(o);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Options_RegisterTheMasterKeyProvider()
    {
        using var provider = Host(o =>
        {
            o.HashMasterKey = Convert.ToBase64String(MasterKey);
            o.HashKeyId = "local1";
        });

        var keys = provider.GetRequiredService<IHashKeyProvider>();

        Assert.Equal("local1", keys.ActiveKeyId);
        Assert.Equal(
            HashV2.DeriveTenantKey(MasterKey, "acme", "Account"),
            keys.GetKey(ScopeContext.Tenant("acme"), "Account").ToArray());
    }

    [Fact]
    public void Options_WithoutAKey_RegisterNoProvider()
    {
        using var provider = Host(_ => { });

        Assert.Null(provider.GetService<IHashKeyProvider>());
    }

    [Fact]
    public void AProviderRegisteredByTheApplication_Wins()
    {
        var custom = new MasterKeyHashKeyProvider(MasterKey, "custom");

        using var provider = Host(
            o => o.HashMasterKey = Convert.ToBase64String(MasterKey),
            services => services.AddSingleton<IHashKeyProvider>(custom));

        Assert.Same(custom, provider.GetRequiredService<IHashKeyProvider>());
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Options_RejectAnInvalidMasterKeyAtStartup(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => Host(o => o.HashMasterKey = key));
    }
}
