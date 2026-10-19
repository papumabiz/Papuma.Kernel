// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// The key-check value of the keyed <c>Hash</c> policy (ADR-025, point 5) against a real SQLite
/// file — the counterpart of <c>HashKeyCheckTests</c> in the PostgreSQL suite.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteHashKeyCheckTests(SqliteFixture fixture) : IAsyncLifetime
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static readonly byte[] OtherKey = Convert.FromHexString(
        "1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");

    private static string NewKeyId() => "t" + Guid.NewGuid().ToString("N")[..12];

    private sealed record Account(string Id, [property: TrackHash] string Secret);

    public async Task InitializeAsync() =>
        await fixture.CreateStoreAsync(new KernelModelBuilder().Document<Account>().Build()); // creates the schema

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<SqliteConnection> OpenAsync() => await SqliteConnectionFactory.OpenAsync(fixture.ConnectionString);

    private async Task<string?> StoredAsync(string keyId)
    {
        await using var connection = await OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT check_value FROM hash_key_check WHERE kid = @kid";
        cmd.Parameters.AddWithValue("@kid", keyId);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private async Task EnsureAsync(byte[] key, string keyId)
    {
        await using var connection = await OpenAsync();
        await SqliteSchemaManager.EnsureHashKeyAsync(connection, new MasterKeyHashKeyProvider(key, keyId));
    }

    [Fact]
    public async Task TheFirstStart_StoresTheCheckValue_AndALaterStartWithTheSameKeyPasses()
    {
        var keyId = NewKeyId();

        await EnsureAsync(MasterKey, keyId);
        await EnsureAsync(MasterKey, keyId);

        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(keyId));
    }

    [Fact]
    public async Task ADifferentKeyUnderTheSameKeyId_IsRefused_AndDoesNotOverwrite()
    {
        var keyId = NewKeyId();
        await EnsureAsync(MasterKey, keyId);

        var ex = await Assert.ThrowsAsync<HashKeyMismatchException>(() => EnsureAsync(OtherKey, keyId));

        Assert.Equal(keyId, ex.KeyId);
        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(keyId));
    }

    [Fact]
    public async Task ANewKeyId_IsARotation_AndLeavesTheOldRowAlone()
    {
        var oldId = NewKeyId();
        var newId = NewKeyId();
        await EnsureAsync(MasterKey, oldId);

        await EnsureAsync(OtherKey, newId);

        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(oldId));
        Assert.Equal(HashV2.KeyCheck(OtherKey), await StoredAsync(newId));
    }

    [Fact]
    public async Task AFileWithoutTheTable_GetsAClearError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"papuma_nokey_{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    SqliteSchemaManager.EnsureHashKeyAsync(connection, new MasterKeyHashKeyProvider(MasterKey, "k1")));
                Assert.Contains("hash_key_check", ex.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private IHost Host(byte[] key, string keyId, bool ensureSchema = true)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPapumaKernelLocal(o =>
        {
            o.ConnectionString = fixture.ConnectionString;
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
        using (var first = Host(MasterKey, keyId))
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        Assert.Equal(HashV2.KeyCheck(MasterKey), await StoredAsync(keyId));

        using var second = Host(OtherKey, keyId);
        await Assert.ThrowsAsync<HashKeyMismatchException>(() => second.StartAsync());
    }

    [Fact]
    public async Task TheHostedStartup_VerifiesTheKey_EvenWhenTheSchemaIsEnsuredElsewhere()
    {
        var keyId = NewKeyId();
        await EnsureAsync(MasterKey, keyId);

        using var host = Host(OtherKey, keyId, ensureSchema: false);

        await Assert.ThrowsAsync<HashKeyMismatchException>(() => host.StartAsync());
    }
}
