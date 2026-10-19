// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// The key-check value of the keyed <c>Hash</c> policy (ADR-025, point 5) against a real
/// PostgreSQL: stored by the first instance for a key id, verified by every later one — directly
/// and through the hosted startup.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HashKeyCheckTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static readonly byte[] OtherKey = Convert.FromHexString(
        "1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");

    // The database is shared by the collection, so every test uses its own key id.
    private static string NewKeyId() => "t" + Guid.NewGuid().ToString("N")[..12];

    private sealed record Account(string Id, [property: TrackHash] string Secret);

    public async Task InitializeAsync() =>
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, new KernelModelBuilder().Document<Account>().Build());

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string?> StoredAsync(string keyId)
    {
        await using var cmd = fixture.DataSource.CreateCommand("SELECT check_value FROM papuma.hash_key_check WHERE kid = @kid");
        cmd.Parameters.AddWithValue("kid", keyId);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task TheFirstStart_StoresTheCheckValue_AndALaterStartWithTheSameKeyPasses()
    {
        var keyId = NewKeyId();
        var provider = new MasterKeyHashKeyProvider(MasterKey, keyId);

        await SchemaManager.EnsureHashKeyAsync(fixture.DataSource, provider);
        await SchemaManager.EnsureHashKeyAsync(fixture.DataSource, new MasterKeyHashKeyProvider(MasterKey, keyId));

        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(keyId));
    }

    [Fact]
    public async Task ADifferentKeyUnderTheSameKeyId_IsRefused_AndDoesNotOverwrite()
    {
        var keyId = NewKeyId();
        await SchemaManager.EnsureHashKeyAsync(fixture.DataSource, new MasterKeyHashKeyProvider(MasterKey, keyId));

        var ex = await Assert.ThrowsAsync<HashKeyMismatchException>(
            () => SchemaManager.EnsureHashKeyAsync(fixture.DataSource, new MasterKeyHashKeyProvider(OtherKey, keyId)));

        Assert.Equal(keyId, ex.KeyId);
        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(keyId));
    }

    [Fact]
    public async Task ANewKeyId_IsARotation_AndLeavesTheOldRowAlone()
    {
        var oldId = NewKeyId();
        var newId = NewKeyId();
        await SchemaManager.EnsureHashKeyAsync(fixture.DataSource, new MasterKeyHashKeyProvider(MasterKey, oldId));

        await SchemaManager.EnsureHashKeyAsync(fixture.DataSource, new MasterKeyHashKeyProvider(OtherKey, newId));

        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(oldId));
        Assert.Equal(HashV2.KeyCheck(OtherKey), await StoredAsync(newId));
    }

    [Fact]
    public async Task ConcurrentFirstStarts_AllAgreeOnTheWinner()
    {
        var keyId = NewKeyId();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            SchemaManager.EnsureHashKeyAsync(fixture.DataSource, new MasterKeyHashKeyProvider(MasterKey, keyId))));

        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(keyId));
    }

    private static IHost Host(NpgsqlDataSource dataSource, byte[] key, string keyId, bool ensureSchema = true)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPapumaKernel(o =>
        {
            o.DataSource = dataSource;
            o.Model(m => m.Document<Account>());
            o.EnsureSchema = ensureSchema;
            o.RunFeedWorkers = false;
            o.HashMasterKey = Convert.ToBase64String(key);
            o.HashKeyId = keyId;
        });
        return builder.Build();
    }

    [Fact]
    public async Task TheHostedStartup_RecordsTheKey_AndRefusesAnInstanceWithAnotherKey()
    {
        var keyId = NewKeyId();
        using (var first = Host(fixture.DataSource, MasterKey, keyId))
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(keyId));

        using var second = Host(fixture.DataSource, OtherKey, keyId);
        var ex = await Assert.ThrowsAsync<HashKeyMismatchException>(() => second.StartAsync());
        Assert.Equal(keyId, ex.KeyId);
    }

    [Fact]
    public async Task TheHostedStartup_VerifiesTheKey_EvenWhenTheSchemaIsEnsuredElsewhere()
    {
        var keyId = NewKeyId();
        await SchemaManager.EnsureHashKeyAsync(fixture.DataSource, new MasterKeyHashKeyProvider(MasterKey, keyId));

        using var host = Host(fixture.DataSource, OtherKey, keyId, ensureSchema: false);

        await Assert.ThrowsAsync<HashKeyMismatchException>(() => host.StartAsync());
    }
}
