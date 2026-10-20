// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// Event payload policies on a path and on one of its descendants give one result, whatever order they were
/// declared in (review C-3): the deepest path is applied first.
/// </summary>
public sealed class EventPolicyOrderTests
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static readonly ScopeContext Acme = ScopeContext.Tenant("acme");

    private sealed record Address(string Street, string City);

    private sealed record Moved(string UserId, Address Address);

    private static JsonObject Payload() =>
        (JsonObject)JsonNode.Parse("""{"userId":"u1","address":{"street":"Main St 1","city":"Bonn"}}""")!;

    private static JsonObject Apply(Action<KernelModelBuilder.EventTypeBuilder<Moved>> configure)
    {
        var model = new KernelModelBuilder().Event(configure).Build();
        return EventPayloadPolicyApplier.Apply(
            Payload(), model.GetRequiredEvent<Moved>(), new MasterKeyHashKeyProvider(MasterKey, "k1"), Acme);
    }

    private static string ExpectedAddressHash() =>
        HashV2.Hash(
            HashV2.DeriveTenantKey(MasterKey, "acme", "Moved"),
            "k1",
            JsonNode.Parse("""{"city":"Bonn"}"""));

    [Fact]
    public void HashOnTheParent_RedactOnTheChild_HashesTheParentWithoutTheRedactedField()
    {
        var stored = Apply(e => e.Property(x => x.Address).StoreAsHash().Property(x => x.Address.Street).Redact());

        Assert.Equal(ExpectedAddressHash(), (string?)stored["address"]);
        Assert.DoesNotContain("Main St", stored.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeclarationOrder_DoesNotChangeTheResult()
    {
        var parentFirst = Apply(e => e.Property(x => x.Address).StoreAsHash().Property(x => x.Address.Street).Redact());
        var childFirst = Apply(e => e.Property(x => x.Address.Street).Redact().Property(x => x.Address).StoreAsHash());

        Assert.Equal(parentFirst.ToJsonString(), childFirst.ToJsonString());
    }

    [Fact]
    public void RedactOnTheParent_HashOnTheChild_LeavesNothingOfTheParent_InEitherOrder()
    {
        var parentFirst = Apply(e => e.Property(x => x.Address).Redact().Property(x => x.Address.City).StoreAsHash());
        var childFirst = Apply(e => e.Property(x => x.Address.City).StoreAsHash().Property(x => x.Address).Redact());

        Assert.False(parentFirst.ContainsKey("address"));
        Assert.Equal(parentFirst.ToJsonString(), childFirst.ToJsonString());
    }

    [Fact]
    public void PoliciesOnUnrelatedPaths_AreAllApplied()
    {
        var stored = Apply(e => e.Property(x => x.UserId).StoreAsHash().Property(x => x.Address.Street).Redact());

        Assert.NotEqual("u1", (string?)stored["userId"]);
        Assert.False(stored["address"]!.AsObject().ContainsKey("street"));
        Assert.Equal("Bonn", (string?)stored["address"]!["city"]);
    }
}
