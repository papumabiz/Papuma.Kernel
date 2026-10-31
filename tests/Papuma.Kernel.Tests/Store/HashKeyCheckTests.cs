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
        "7c1e94d20a5f3b86e4219dc07f6a58b3d1940e2c6b8f7a35c2e90d4b1f6a8c27");

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

    [Fact]
    public async Task ACheckRowThatIsNeitherStoredNorReadable_FailsWithAClearMessage()
    {
        // A trigger that swallows the insert stands in for a row that vanishes between insert and read.
        await using var scratch = await ScratchPostgresDatabase.CreateAsync(fixture.Database);
        await SchemaManager.EnsureSchemaAsync(scratch.DataSource);
        await scratch.ExecuteAsync("""
            CREATE FUNCTION papuma.swallow_insert() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NULL; END';
            CREATE TRIGGER swallow BEFORE INSERT ON papuma.hash_key_check
                FOR EACH ROW EXECUTE FUNCTION papuma.swallow_insert();
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaManager.EnsureHashKeyAsync(
            scratch.DataSource, new MasterKeyHashKeyProvider(MasterKey, NewKeyId())));

        Assert.Contains("hash_key_check", ex.Message, StringComparison.Ordinal);
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
