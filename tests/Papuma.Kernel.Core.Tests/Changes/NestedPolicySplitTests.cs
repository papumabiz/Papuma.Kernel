// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// Regression for the nested-policy leak: a policy declared below a diff entry's path (<c>address.street</c>)
/// must still apply when the entry carries the whole object (<c>address</c>) — on insert, delete, a field added
/// or removed with an object value, and a type change to or from an object. The entry is split so the protected
/// subtree gets an entry of its own.
/// </summary>
public sealed class NestedPolicySplitTests
{
    private const string Secret = "SECRET-STREET";

    private sealed record Customer(string Id, string Name, Address? Address);

    private sealed record Address([property: SensitiveData] string Street, string City);

    private static readonly DocumentTypeMetadata CustomerMetadata =
        new KernelModelBuilder().Document<Customer>().Build().GetRequired<Customer>();

    private static readonly MasterKeyHashKeyProvider HashKeys = new(
        Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"), "k1");

    private static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly JsonObject WithAddress =
        Doc($$$"""{"id":"1","name":"Harry","address":{"street":"{{{Secret}}}","city":"Bonn"}}""");

    private static readonly JsonObject WithoutAddress = Doc("""{"id":"1","name":"Harry","address":null}""");

    private static DocumentDiff Policed(JsonObject? before, JsonObject? after, DocumentTypeMetadata? metadata = null) =>
        PolicyApplier.Apply(JsonDiffEngine.Diff(before, after), metadata ?? CustomerMetadata, "1", HashKeys, ScopeContext.Tenant("acme"));

    private static DocumentTypeMetadata Metadata(params (string Path, FieldPolicy Policy)[] policies) => new(
        "Customer", typeof(object), policies.ToDictionary(p => p.Path, p => p.Policy, StringComparer.Ordinal),
        keys: [], idGetter: _ => "1", upcasters: [], validator: null, exposedToMcp: false);

    [Fact]
    public void TheModel_DeclaresThePolicyOnTheNestedPath()
    {
        Assert.Equal(FieldPolicy.Redact, CustomerMetadata.Policies["address.street"]);
    }

    [Fact]
    public void AnInsert_SplitsTheProtectedFieldOutOfItsContainer()
    {
        var diff = Policed(null, WithAddress);

        Assert.Equal(["address", "address.street", "id", "name"], diff.Paths);
        Assert.Equal("""{"city":"Bonn"}""", diff.Entries["address"].New!.ToJsonString());
        Assert.False(diff.Entries["address"].HasOld);
        Assert.Equal(DiffEntryKind.Redacted, diff.Entries["address.street"].Kind);
        Assert.DoesNotContain(Secret, diff.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADelete_SplitsTheProtectedFieldOutOfItsContainer()
    {
        var diff = Policed(WithAddress, null);

        Assert.Equal(["address", "address.street", "id", "name"], diff.Paths);
        Assert.Equal("""{"city":"Bonn"}""", diff.Entries["address"].Old!.ToJsonString());
        Assert.False(diff.Entries["address"].HasNew);
        Assert.Equal(DiffEntryKind.Redacted, diff.Entries["address.street"].Kind);
        Assert.DoesNotContain(Secret, diff.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingAndClearingAnObject_SplitsTheProtectedField(bool set)
    {
        var diff = set ? Policed(WithoutAddress, WithAddress) : Policed(WithAddress, WithoutAddress);

        Assert.Equal(["address", "address.street"], diff.Paths);
        var container = diff.Entries["address"];
        Assert.True(container.HasOld && container.HasNew);
        Assert.Equal(set ? null : """{"city":"Bonn"}""", container.Old?.ToJsonString());
        Assert.Equal(set ? """{"city":"Bonn"}""" : null, container.New?.ToJsonString());
        Assert.Equal(DiffEntryKind.Redacted, diff.Entries["address.street"].Kind);
        Assert.DoesNotContain(Secret, diff.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeOfOnlyTheProtectedField_IsUnchanged()
    {
        var after = Doc("""{"id":"1","name":"Harry","address":{"street":"OTHER","city":"Bonn"}}""");

        var diff = Policed(WithAddress, after);

        Assert.Equal("""{"address.street":{"changed":true}}""", diff.ToJson().ToJsonString());
    }

    [Fact]
    public void AnEntryWithoutAPolicyBelowIt_IsNotSplit()
    {
        var before = Doc("""{"id":"1","tags":null}""");
        var after = Doc("""{"id":"1","tags":{"a":1,"b":2}}""");

        var diff = Policed(before, after);

        Assert.Equal("""{"tags":{"old":null,"new":{"a":1,"b":2}}}""", diff.ToJson().ToJsonString());
    }

    [Fact]
    public void ADeeplyNestedPolicy_KeepsTheRestOfTheSubtreeInTheContainer()
    {
        var metadata = Metadata(("a.b.c", FieldPolicy.Redact));

        var diff = Policed(null, Doc($$$"""{"a":{"b":{"c":"{{{Secret}}}","d":1},"e":2}}"""), metadata);

        Assert.Equal(["a", "a.b.c"], diff.Paths);
        Assert.Equal("""{"b":{"d":1},"e":2}""", diff.Entries["a"].New!.ToJsonString());
        Assert.Equal(DiffEntryKind.Redacted, diff.Entries["a.b.c"].Kind);
    }

    [Fact]
    public void TheNearestDeclaredPathWins_AlsoInsideASplitContainer()
    {
        var metadata = Metadata(("address", FieldPolicy.Redact), ("address.city", FieldPolicy.Track));

        var diff = Policed(null, WithAddress, metadata);

        Assert.Equal(DiffEntryKind.Redacted, diff.Entries["address"].Kind);
        Assert.Equal("Bonn", (string?)diff.Entries["address.city"].New);
        Assert.DoesNotContain(Secret, diff.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FieldPolicy.DoNotTrack)]
    [InlineData(FieldPolicy.Hash)]
    [InlineData(FieldPolicy.Reference)]
    public void EveryPolicyKind_AppliesToTheSplitField(FieldPolicy policy)
    {
        var metadata = Metadata(("address.street", policy));

        var diff = Policed(null, WithAddress, metadata);

        Assert.Equal("""{"city":"Bonn"}""", diff.Entries["address"].New!.ToJsonString());
        Assert.DoesNotContain(Secret, diff.ToJson().ToJsonString(), StringComparison.Ordinal);
        switch (policy)
        {
            case FieldPolicy.DoNotTrack:
                Assert.DoesNotContain("address.street", diff.Paths);
                break;
            case FieldPolicy.Hash:
                Assert.Matches("^v2:k1:[0-9a-f]{64}$", diff.Entries["address.street"].Hash);
                break;
            default:
                Assert.Equal("Customer/1/address.street", diff.Entries["address.street"].Reference);
                break;
        }
    }

    [Fact]
    public void AKeyWithADot_IsSplitAsOneSegment()
    {
        var metadata = Metadata(("addresses.home\\.main.street", FieldPolicy.Redact));

        var document = Doc("""{"addresses":{"home.main":{"street":"SECRET-STREET","city":"Bonn"},"work":{"street":"Open"}}}""");

        var diff = Policed(null, document, metadata);

        Assert.Equal(["addresses", "addresses.home\\.main.street"], diff.Paths);
        Assert.Equal("""{"home.main":{"city":"Bonn"},"work":{"street":"Open"}}""", diff.Entries["addresses"].New!.ToJsonString());
    }

    public static TheoryData<string?, string?> ReversibleCases => new()
    {
        { null, """{"id":"1","address":{"street":"S","city":"Bonn"}}""" },
        { """{"id":"1","address":{"street":"S","city":"Bonn"}}""", null },
        { """{"id":"1","address":null}""", """{"id":"1","address":{"street":"S","city":"Bonn"}}""" },
        { """{"id":"1","address":{"street":"S","city":"Bonn"}}""", """{"id":"1","address":"moved"}""" },
        { """{"id":"1","address":{"street":"S"}}""", """{"id":"1","address":[1,2]}""" },
        { """{"id":"1"}""", """{"id":"1","address":{"street":"S"}}""" },
        { """{"id":"1","address":{"street":{"nested":true},"city":"Bonn"}}""", """{"id":"1"}""" },
    };

    // The split itself (before the policies turn entries into markers) must still describe both states exactly.
    [Theory]
    [MemberData(nameof(ReversibleCases))]
    public void TheSplitDiff_StillRebuildsBothStates(string? beforeJson, string? afterJson)
    {
        var before = beforeJson is null ? null : Doc(beforeJson);
        var after = afterJson is null ? null : Doc(afterJson);

        var split = PolicyApplier.SplitAtPolicies(JsonDiffEngine.Diff(before, after), CustomerMetadata);

        Assert.Contains("address.street", split.Paths);
        Assert.True(JsonNode.DeepEquals(after ?? new JsonObject(), JsonDiffEngine.Apply(before, split)), "Apply");
        Assert.True(JsonNode.DeepEquals(before ?? new JsonObject(), JsonDiffEngine.ApplyReverse(after, split)), "ApplyReverse");
    }
}
