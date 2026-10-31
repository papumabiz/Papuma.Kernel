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
        "7c1e94d20a5f3b86e4219dc07f6a58b3d1940e2c6b8f7a35c2e90d4b1f6a8c27");

    private sealed record Account(string Id, string Name);

    private static ServiceProvider Host(Action<PapumaKernelLocalOptions> configure, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddPapumaKernelLocal(o =>
        {
            o.ConnectionString = "Data Source=hash-key-options.db"; // never opened: EnsureSchema is off, no worker runs
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
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")] // the published test key (review P-16)
    public void Options_RejectAnInvalidMasterKeyAtStartup(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => Host(o => o.HashMasterKey = key));
    }
}
