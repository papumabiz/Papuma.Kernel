// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Serialization;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Review N-14 through a real PostgreSQL session: <c>[SensitiveData]</c> on a member of a <c>[JsonDerivedType]</c>
/// subtype was silently ignored — the scan followed the declared (base) type — and the card number went into the
/// change feed in clear text, next to the <c>$type</c> discriminator.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PolymorphicPolicyFeedTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record Order(string Id, string Customer, Payment Payment);

    [JsonDerivedType(typeof(Card), "card")]
    [JsonDerivedType(typeof(Transfer), "transfer")]
    private abstract record Payment(decimal Amount);

    private sealed record Card(decimal Amount, [property: SensitiveData] string Pan) : Payment(Amount);

    private sealed record Transfer(decimal Amount, [property: SensitiveData] string Iban) : Payment(Amount);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Order>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ADerivedTypesSensitiveField_NeverReachesTheFeed_InAnyWriteOfItsContainer()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = Guid.NewGuid().ToString("N");

        var results = new List<SaveResult>
        {
            await session.SaveAsync(new Order(id, "Ann", new Card(10m, "SECRET-PAN-1")), 0),        // insert
            await session.SaveAsync(new Order(id, "Ann", new Card(10m, "SECRET-PAN-2")), 1),        // field change
            await session.SaveAsync(new Order(id, "Ann", new Transfer(10m, "SECRET-IBAN")), 2),     // subtype change
        };
        results.Add(await session.DeleteAsync<Order>(id, 3));                                      // delete

        var history = await session.GetHistoryAsync<Order>(id);

        Assert.Equal(4, history.Count);
        foreach (var diff in results.Select(r => r.Diff).Concat(history.Select(h => h.Diff)))
        {
            Assert.DoesNotContain("SECRET", diff.ToJson().ToJsonString(), StringComparison.Ordinal);
        }

        Assert.Equal(DiffEntryKind.Redacted, history[0].Diff.Entries["payment.pan"].Kind);
        Assert.Equal(DiffEntryKind.Redacted, history[1].Diff.Entries["payment.pan"].Kind);
        Assert.Equal(DiffEntryKind.Redacted, history[2].Diff.Entries["payment.iban"].Kind);

        Assert.Null(await session.LoadAsync<Order>(id)); // deleted: the document is gone, the history stays
    }

    [Fact]
    public async Task ADerivedType_RoundTripsThroughTheStore()
    {
        await using var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = Guid.NewGuid().ToString("N");

        await session.SaveAsync(new Order(id, "Ann", new Card(10m, "4111")), 0);

        var loaded = await session.LoadAsync<Order>(id);
        Assert.Equal(new Card(10m, "4111"), loaded!.Document.Payment);
    }
}
