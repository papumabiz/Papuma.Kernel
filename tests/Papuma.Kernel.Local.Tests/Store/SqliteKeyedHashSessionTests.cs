// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// The keyed <c>Hash</c> policy (ADR-025) through a real SQLite session — the counterpart of
/// <c>KeyedHashSessionTests</c> in the PostgreSQL suite.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteKeyedHashSessionTests(SqliteFixture fixture) : IAsyncLifetime
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private KernelModel _model = null!;

    private sealed record Account(string Id, string Name, [property: TrackHash] string Secret);

    public async Task InitializeAsync()
    {
        _model = new KernelModelBuilder().Document<Account>(d => d.ExposeToMcp()).Build();
        await fixture.CreateStoreAsync(_model); // creates the schema
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private SqliteDocumentStore Store(IHashKeyProvider? hashKeys) =>
        new(fixture.ConnectionString, _model, notifyWaiters: null, logger: null, hashKeys);

    [Fact]
    public async Task WithAProvider_FeedAndMaskedRead_ShowTheSameKeyedHash()
    {
        var tenant = NewTenant();
        var id = NewId();
        await using var session = Store(new MasterKeyHashKeyProvider(MasterKey, "kid1")).OpenSession(tenant);
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
    public async Task WithoutAProvider_TheLegacyV1HashIsUnchanged()
    {
        var id = NewId();
        await using var session = Store(hashKeys: null).OpenSession(NewTenant());
        await session.SaveAsync(new Account(id, "H", "pw-1"), 0);

        var feed = (await session.GetHistoryAsync<Account>(id))[0].Diff.Entries["secret"];

        Assert.Equal(PolicyHash.Of(JsonValue.Create("pw-1")), feed.Hash);
    }

    [Fact]
    public async Task APatch_OfAHashedField_UsesTheKeyedHash()
    {
        var id = NewId();
        await using var session = Store(new MasterKeyHashKeyProvider(MasterKey, "kid1")).OpenSession(NewTenant());
        await session.SaveAsync(new Account(id, "H", "pw-1"), 0);

        await session.PatchAsync<Account>(id, p => p.Set(x => x.Secret, "pw-2"));

        var history = await session.GetHistoryAsync<Account>(id);
        Assert.StartsWith("v2:kid1:", history[^1].Diff.Entries["secret"].Hash, StringComparison.Ordinal);
    }
}
