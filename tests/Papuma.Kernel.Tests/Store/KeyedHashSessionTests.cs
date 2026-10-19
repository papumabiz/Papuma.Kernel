// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// The keyed <c>Hash</c> policy (ADR-025) through a real PostgreSQL session: a configured key
/// provider makes the feed and the policy-projected read produce the same <c>v2</c> hash; without
/// one the legacy v1 hash is unchanged. Plus the hosting options that configure the provider.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class KeyedHashSessionTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private KernelModel _model = null!;

    private sealed record Account(string Id, string Name, [property: TrackHash] string Secret);

    public async Task InitializeAsync()
    {
        _model = new KernelModelBuilder().Document<Account>(d => d.ExposeToMcp()).Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, _model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task WithAProvider_FeedAndMaskedRead_ShowTheSameKeyedHash()
    {
        var provider = new MasterKeyHashKeyProvider(MasterKey, "kid1");
        var store = new DocumentStore(fixture.DataSource, _model, logger: null, provider);
        var tenant = NewTenant();
        var id = NewId();
        await using var session = store.OpenSession(tenant);
        await session.SaveAsync(new Account(id, "H", "pw-1"), 0);

        var feed = (await session.GetHistoryAsync<Account>(id))[0].Diff.Entries["secret"];
        var masked = await session.LoadMaskedAsync<Account>(id);

        var expected = HashV2.Hash(
            HashV2.DeriveTenantKey(MasterKey, tenant.TenantId!, nameof(Account)), "kid1", JsonValue.Create("pw-1"));
        Assert.Equal(DiffEntryKind.Hashed, feed.Kind);
        Assert.Equal(expected, feed.Hash);
        Assert.Equal(expected, (string?)masked!.Document["secret"]);
    }

    [Fact]
    public async Task WithAProvider_TheSameValueHashesDifferentlyInAnotherTenant()
    {
        var store = new DocumentStore(
            fixture.DataSource, _model, logger: null, new MasterKeyHashKeyProvider(MasterKey, "kid1"));
        async Task<string?> HashInNewTenantAsync()
        {
            await using var session = store.OpenSession(NewTenant());
            var id = NewId();
            await session.SaveAsync(new Account(id, "H", "same-secret"), 0);
            return (await session.GetHistoryAsync<Account>(id))[0].Diff.Entries["secret"].Hash;
        }

        Assert.NotEqual(await HashInNewTenantAsync(), await HashInNewTenantAsync());
    }

    [Fact]
    public async Task WithoutAProvider_TheLegacyV1HashIsUnchanged()
    {
        var store = new DocumentStore(fixture.DataSource, _model);
        var id = NewId();
        await using var session = store.OpenSession(NewTenant());
        await session.SaveAsync(new Account(id, "H", "pw-1"), 0);

        var feed = (await session.GetHistoryAsync<Account>(id))[0].Diff.Entries["secret"];

        Assert.Equal(PolicyHash.Of(JsonValue.Create("pw-1")), feed.Hash);
        Assert.DoesNotContain(':', feed.Hash!);
    }

    [Fact]
    public async Task APatch_OfAHashedField_UsesTheKeyedHash()
    {
        var store = new DocumentStore(
            fixture.DataSource, _model, logger: null, new MasterKeyHashKeyProvider(MasterKey, "kid1"));
        var id = NewId();
        await using var session = store.OpenSession(NewTenant());
        await session.SaveAsync(new Account(id, "H", "pw-1"), 0);

        await session.PatchAsync<Account>(id, p => p.Set(x => x.Secret, "pw-2"));

        var history = await session.GetHistoryAsync<Account>(id);
        Assert.StartsWith("v2:kid1:", history[^1].Diff.Entries["secret"].Hash, StringComparison.Ordinal);
    }

    // ── Hosting options ────────────────────────────────────────────────────────

    private static ServiceProvider Host(Action<PapumaKernelOptions> configure, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddPapumaKernel(o =>
        {
            o.DataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused");
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
            o.HashKeyId = "prod1";
        });

        var keys = provider.GetRequiredService<IHashKeyProvider>();

        Assert.Equal("prod1", keys.ActiveKeyId);
        Assert.Equal(
            HashV2.DeriveTenantKey(MasterKey, "acme", "Account"),
            keys.GetKey(ScopeContext.Tenant("acme"), "Account").ToArray());
        Assert.NotNull(provider.GetRequiredService<DocumentStore>());
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
    [InlineData("AAAA")] // valid base64, 3 bytes
    public void Options_RejectAnInvalidMasterKeyAtStartup(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => Host(o => o.HashMasterKey = key));
    }

    [Fact]
    public void Options_RejectAnInvalidKeyId()
    {
        Assert.ThrowsAny<ArgumentException>(() => Host(o =>
        {
            o.HashMasterKey = Convert.ToBase64String(MasterKey);
            o.HashKeyId = "bad id";
        }));
    }
}
