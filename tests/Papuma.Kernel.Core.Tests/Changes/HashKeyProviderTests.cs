// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// The key provider of the keyed <c>Hash</c> policy (ADR-025) and how the three policy
/// appliers use it: the feed diff, the policy-projected read, and event payloads.
/// </summary>
public sealed class HashKeyProviderTests
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private sealed record Account(string Id, [property: TrackHash] string Secret, string Name);

    private sealed record LoggedIn(string UserId, string Token);

    private sealed record Plain(string Id, string Name);

    private sealed record Ping(string Id);

    private static MasterKeyHashKeyProvider Provider(string keyId = "k1") => new(MasterKey, keyId);

    private static ScopeContext Acme => ScopeContext.Tenant("acme");

    // ── MasterKeyHashKeyProvider ───────────────────────────────────────────────

    [Fact]
    public void GetKey_IsTheDocumentedHkdfDerivation_AndStable()
    {
        var provider = Provider();

        var key = provider.GetKey(Acme, "Account").ToArray();

        Assert.Equal(HashV2.DeriveTenantKey(MasterKey, "acme", "Account"), key);
        Assert.Equal(key, provider.GetKey(Acme, "Account").ToArray());
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void GetKey_DiffersPerTenantAndPerType_AndUsesAnEmptyTenantForPlatformScope()
    {
        var provider = Provider();

        Assert.NotEqual(provider.GetKey(Acme, "Account").ToArray(), provider.GetKey(ScopeContext.Tenant("globex"), "Account").ToArray());
        Assert.NotEqual(provider.GetKey(Acme, "Account").ToArray(), provider.GetKey(Acme, "Order").ToArray());
        Assert.Equal(
            HashV2.DeriveTenantKey(MasterKey, string.Empty, "Account"),
            provider.GetKey(ScopeContext.Platform(), "Account").ToArray());
    }

    [Fact]
    public void Constructor_RejectsAShortMasterKey()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MasterKeyHashKeyProvider(new byte[31], "k1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("colon:")]
    [InlineData("ünï")]
    [InlineData("123456789012345678901234567890123")] // 33 characters
    public void Constructor_RejectsAnInvalidKeyId(string keyId)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MasterKeyHashKeyProvider(MasterKey, keyId));
    }

    [Fact]
    public void FromBase64_ReadsTheConfigurationForm_AndRejectsGarbage()
    {
        var provider = MasterKeyHashKeyProvider.FromBase64(Convert.ToBase64String(MasterKey), "prod_2026-10");

        Assert.Equal("prod_2026-10", provider.ActiveKeyId);
        Assert.Equal(
            Provider().GetKey(Acme, "Account").ToArray(),
            provider.GetKey(Acme, "Account").ToArray());
        Assert.Throws<ArgumentException>(() => MasterKeyHashKeyProvider.FromBase64("not base64!", "k1"));
        Assert.Throws<ArgumentException>(() => MasterKeyHashKeyProvider.FromBase64("  ", "k1"));
    }

    // ── The feed diff ──────────────────────────────────────────────────────────

    [Fact]
    public void PolicyApplier_WithAProvider_WritesTheKeyedHash()
    {
        var metadata = AccountMetadata();
        var diff = JsonDiffEngine.Diff(
            (JsonObject)JsonNode.Parse("""{"secret":"a","name":"x"}""")!,
            (JsonObject)JsonNode.Parse("""{"secret":"b","name":"x"}""")!);

        var applied = PolicyApplier.Apply(diff, metadata, "1", Provider("kid1"), Acme);

        var entry = applied.Entries["secret"];
        Assert.Equal(DiffEntryKind.Hashed, entry.Kind);
        var expected = HashV2.Hash(HashV2.DeriveTenantKey(MasterKey, "acme", "Account"), "kid1", JsonValue.Create("b"));
        Assert.Equal(expected, entry.Hash);
        Assert.StartsWith("v2:kid1:", entry.Hash, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyApplier_WithoutAProvider_FailsClosed_InsteadOfWritingAnUnkeyedHash()
    {
        var diff = JsonDiffEngine.Diff(
            (JsonObject)JsonNode.Parse("""{"secret":"a"}""")!,
            (JsonObject)JsonNode.Parse("""{"secret":"b"}""")!);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PolicyApplier.Apply(diff, AccountMetadata(), "1", hashKeys: null, Acme));

        Assert.Contains("IHashKeyProvider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyApplier_WithoutAProvider_IsFineWhenNoHashedFieldChanged()
    {
        var diff = JsonDiffEngine.Diff(
            (JsonObject)JsonNode.Parse("""{"secret":"a","name":"x"}""")!,
            (JsonObject)JsonNode.Parse("""{"secret":"a","name":"y"}""")!);

        var applied = PolicyApplier.Apply(diff, AccountMetadata(), "1", hashKeys: null, Acme);

        Assert.Equal(DiffEntryKind.Tracked, applied.Entries["name"].Kind);
        Assert.DoesNotContain("secret", applied.Paths);
    }

    [Fact]
    public void AHashStoredByV1_IsStillReadable_AsAnOpaqueString()
    {
        const string v1 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        var wire = (JsonObject)JsonNode.Parse("{\"secret\":{\"changed\":true,\"hash\":\"" + v1 + "\"}}")!;

        var diff = DocumentDiff.FromJson(wire);

        Assert.Equal(v1, diff.Entries["secret"].Hash);
        Assert.True(JsonNode.DeepEquals(wire, diff.ToJson()));
    }

    [Fact]
    public void PolicyApplier_HashesEqualValuesEqually_AndDifferentTenantsDifferently()
    {
        var provider = Provider();
        string HashIn(ScopeContext scope, string newJson)
        {
            var diff = JsonDiffEngine.Diff(
                (JsonObject)JsonNode.Parse("""{"secret":"a"}""")!,
                (JsonObject)JsonNode.Parse(newJson)!);
            return PolicyApplier.Apply(diff, AccountMetadata(), "1", provider, scope).Entries["secret"].Hash!;
        }

        Assert.Equal(HashIn(Acme, """{"secret":"b"}"""), HashIn(Acme, """{"secret":"b"}"""));
        Assert.NotEqual(HashIn(Acme, """{"secret":"b"}"""), HashIn(ScopeContext.Tenant("globex"), """{"secret":"b"}"""));
    }

    [Fact]
    public void PolicyApplier_FailsLoudly_ForAValueWithoutACanonicalForm()
    {
        var diff = JsonDiffEngine.Diff(
            (JsonObject)JsonNode.Parse("""{"secret":1}""")!,
            (JsonObject)JsonNode.Parse("""{"secret":1e5000}""")!);

        var ex = Assert.Throws<CanonicalJsonException>(
            () => PolicyApplier.Apply(diff, AccountMetadata(), "1", Provider(), Acme));

        Assert.Equal("number-too-large", ex.Code);
    }

    // ── The policy-projected read ──────────────────────────────────────────────

    [Fact]
    public void PolicyProjector_ShowsExactlyTheHashTheFeedShows()
    {
        var provider = Provider();
        var metadata = AccountMetadata();
        var document = (JsonObject)JsonNode.Parse("""{"id":"1","secret":"b","name":"x"}""")!;
        var feed = PolicyApplier.Apply(
            JsonDiffEngine.Diff(null, document), metadata, "1", provider, Acme).Entries["secret"].Hash;

        var masked = PolicyProjector.Apply(document, metadata, provider, Acme);

        Assert.Equal(feed, (string?)masked["secret"]);
        Assert.Equal("x", (string?)masked["name"]);
    }

    [Fact]
    public void PolicyProjector_UsesTheScopeOfTheRead()
    {
        var provider = Provider();
        var document = (JsonObject)JsonNode.Parse("""{"id":"1","secret":"b","name":"x"}""")!;

        var inAcme = PolicyProjector.Apply(document, AccountMetadata(), provider, Acme);
        var inGlobex = PolicyProjector.Apply(document, AccountMetadata(), provider, ScopeContext.Tenant("globex"));

        Assert.NotEqual((string?)inAcme["secret"], (string?)inGlobex["secret"]);
    }

    // ── Event payloads ─────────────────────────────────────────────────────────

    [Fact]
    public void EventPayload_HashPolicy_UsesTheKeyedHash_WithTheEventTypeAsTheType()
    {
        var model = new KernelModelBuilder().Event<LoggedIn>(e => e.Property(x => x.Token).StoreAsHash()).Build();
        var metadata = model.GetRequiredEvent<LoggedIn>();
        var payload = (JsonObject)JsonNode.Parse("""{"userId":"u1","token":"t-1"}""")!;

        var stored = EventPayloadPolicyApplier.Apply(payload, metadata, Provider("e1"), Acme);

        var expected = HashV2.Hash(HashV2.DeriveTenantKey(MasterKey, "acme", metadata.Name), "e1", JsonValue.Create("t-1"));
        Assert.Equal(expected, (string?)stored["token"]);
        Assert.Equal("u1", (string?)stored["userId"]);
    }

    [Fact]
    public void EventPayload_HashPolicy_WithoutAProvider_FailsClosed()
    {
        var model = new KernelModelBuilder().Event<LoggedIn>(e => e.Property(x => x.Token).StoreAsHash()).Build();
        var payload = (JsonObject)JsonNode.Parse("""{"userId":"u1","token":"t-1"}""")!;

        Assert.Throws<InvalidOperationException>(
            () => EventPayloadPolicyApplier.Apply(payload, model.GetRequiredEvent<LoggedIn>(), hashKeys: null, Acme));
    }

    // ── Fail closed: the model check ───────────────────────────────────────────

    [Fact]
    public void ModelWithAHashPolicy_WithoutAProvider_IsRefused_NamingTheFields()
    {
        var model = new KernelModelBuilder()
            .Document<Account>()
            .Event<LoggedIn>(e => e.Property(x => x.Token).StoreAsHash())
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => model.EnsureHashKeysConfigured(null));

        Assert.Contains("Account.secret (document)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LoggedIn.token (event)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("HashMasterKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TestHashKeys", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelWithAHashPolicy_WithAProvider_IsAccepted()
    {
        var model = new KernelModelBuilder().Document<Account>().Build();

        model.EnsureHashKeysConfigured(Provider());
    }

    [Fact]
    public void ModelWithoutAHashPolicy_NeedsNoProvider()
    {
        var model = new KernelModelBuilder().Document<Plain>().Event<Ping>().Build();

        model.EnsureHashKeysConfigured(null);
    }

    private static DocumentTypeMetadata AccountMetadata() =>
        new KernelModelBuilder().Document<Account>().Build().GetRequired<Account>();
}
