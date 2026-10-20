// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// Policy attributes must not be silently ignored (review C-1, C-16, C-19, C-20, N-9): they reach members of
/// structs, a collection of annotated elements is a build error unless the collection itself is decided, more than
/// one policy attribute on a property is a build error, and a declaration on a member that is never serialized —
/// or an event key — is refused instead of dropped.
/// </summary>
public sealed class PolicyDiscoveryTests
{
    // ── Types ──────────────────────────────────────────────────────────────────

    public record struct StructAddress([property: SensitiveData] string Street, string City);

    public record struct PlainStructAddress(string Street, string City);

    public record ClassAddress([property: SensitiveData] string Street, string City);

    public record PlainAddress(string Street, string City);

    public record Inner([property: SensitiveData] string Secret);

    public record struct Middle(Inner Inner);

    public record DocStruct(string Id, StructAddress Home);

    public record DocNullableStruct(string Id, StructAddress? Maybe);

    public record DocDeepStruct(string Id, Middle? Middle);

    public record DocPlainNullableStruct(string Id, PlainStructAddress? Maybe);

    public record DocList(string Id, List<ClassAddress> Homes);

    public record DocArray(string Id, ClassAddress[] Homes);

    public record DocDictionary(string Id, Dictionary<string, ClassAddress> Homes);

    public record DocReadOnlyList(string Id, IReadOnlyList<ClassAddress> Homes);

    public record DocEnumerable(string Id, IEnumerable<StructAddress> Homes);

    public record DocListOfLists(string Id, List<List<ClassAddress>> Homes);

    public record DocListInObject(string Id, Holder Holder);

    public record Holder(List<ClassAddress> Homes);

    public record DocPlainList(string Id, List<PlainAddress> Homes, List<string> Tags, int[] Numbers);

    public record DocAttributeOnList(string Id, [property: SensitiveData] List<ClassAddress> Homes);

    public record DocAnnotatedAncestor(string Id, [property: SensitiveData] Holder Holder);

    public record EventStruct(string UserId, StructAddress Home);

    public record EventList(string UserId, List<ClassAddress> Homes);

    public record EventWithUniqueKey(string UserId, [property: UniqueKey] string Email);

    public record EventWithLookupKey(string UserId, [property: LookupKey] string Email);

    public record ConflictHashAndRedact(string Id, [property: SensitiveData, TrackHash] string Secret);

    public record ConflictDoNotTrackAndHash(string Id, [property: DoNotTrack, TrackHash] string Secret);

    public record ConflictReferenceAndRedact(string Id, [property: TrackReference, SensitiveData] string Secret);

    public record ConflictAllFour(
        string Id, [property: DoNotTrack, TrackHash, TrackReference, SensitiveData] string Secret);

    public record SingleAttribute(string Id, [property: TrackHash] string Secret, [property: SensitiveData] string Pin);

    public record DocIgnored(
        string Id,
        [property: JsonIgnore] string Hidden,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sometimes);

    // ── C-1: structs ───────────────────────────────────────────────────────────

    [Fact]
    public void AStructMember_WithAPolicyAttribute_GetsItsPolicy()
    {
        var policies = new KernelModelBuilder().Document<DocStruct>().Build().GetRequired<DocStruct>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["home.street"]);
    }

    [Fact]
    public void ANullableStructMember_GetsItsPolicy()
    {
        var policies = new KernelModelBuilder().Document<DocNullableStruct>().Build().GetRequired<DocNullableStruct>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["maybe.street"]);
    }

    [Fact]
    public void AClassInsideANullableStruct_IsReached()
    {
        var policies = new KernelModelBuilder().Document<DocDeepStruct>().Build().GetRequired<DocDeepStruct>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["middle.inner.secret"]);
    }

    [Fact]
    public void AStructMembersSecret_DoesNotReachTheFeedDiagnosticDiff()
    {
        var model = new KernelModelBuilder().Document<DocStruct>().Build();
        var json = JsonSerializer.SerializeToNode(new DocStruct("1", new StructAddress("SECRET-STREET", "Bonn")), KernelJson.Options)!.AsObject();

        var diff = PolicyApplier.Apply(
            JsonDiffEngine.Diff(new JsonObject(), json), model.GetRequired<DocStruct>(), "1", hashKeys: null, ScopeContext.Tenant("tenant1"));

        var feed = diff.ToJson().ToJsonString();
        Assert.DoesNotContain("SECRET-STREET", feed, StringComparison.Ordinal);
        Assert.Contains("Bonn", feed, StringComparison.Ordinal); // the rest stays tracked
    }

    [Fact]
    public void AnEventStructMember_GetsItsPolicy()
    {
        var policies = new KernelModelBuilder().Event<EventStruct>().Build().GetRequiredEvent<EventStruct>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["home.street"]);
    }

    // ── C-1: collection elements ───────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(DocList), "homes")]
    [InlineData(typeof(DocArray), "homes")]
    [InlineData(typeof(DocDictionary), "homes")]
    [InlineData(typeof(DocReadOnlyList), "homes")]
    [InlineData(typeof(DocEnumerable), "homes")]
    [InlineData(typeof(DocListOfLists), "homes")]
    [InlineData(typeof(DocListInObject), "holder.homes")]
    public void ACollectionOfAnnotatedElements_IsABuildError_NamingPathAndMember(Type documentType, string path)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildDocument(documentType));

        Assert.Contains($"'{path}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[SensitiveData]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Document type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("atomic", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventCollectionOfAnnotatedElements_IsABuildError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KernelModelBuilder().Event<EventList>().Build());

        Assert.Contains("Event type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'homes'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACollectionWithoutPolicyAttributesInside_IsFine()
    {
        var model = new KernelModelBuilder().Document<DocPlainList>().Build();

        Assert.Empty(model.GetRequired<DocPlainList>().Policies);
    }

    [Fact]
    public void ACollection_DecidedByAnAttributeOnItself_IsFine()
    {
        var policies = new KernelModelBuilder().Document<DocAttributeOnList>().Build().GetRequired<DocAttributeOnList>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["homes"]);
    }

    [Fact]
    public void ACollection_DecidedByAFluentPolicy_IsFine()
    {
        var policies = new KernelModelBuilder()
            .Document<DocList>(d => d.Property(x => x.Homes).Redact())
            .Build().GetRequired<DocList>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["homes"]);
    }

    [Fact]
    public void ACollection_ExplicitlyTracked_IsAnAcknowledgement()
    {
        // "The elements are stored as they are" — said on purpose, so the attribute on the element type is not
        // reported. This is the way out for a shared Address type used both as a field and in a list.
        var model = new KernelModelBuilder().Document<DocList>(d => d.Property(x => x.Homes).Track()).Build();

        Assert.False(model.GetRequired<DocList>().Policies.ContainsKey("homes"));
    }

    [Fact]
    public void ACollection_BeneathAnAnnotatedAncestor_IsDecided()
    {
        var model = new KernelModelBuilder().Document<DocAnnotatedAncestor>().Build();

        Assert.Equal(FieldPolicy.Redact, model.GetRequired<DocAnnotatedAncestor>().Policies["holder"]);
    }

    [Fact]
    public void ACollection_BeneathAFluentlyDecidedAncestor_IsDecided() =>
        new KernelModelBuilder().Document<DocListInObject>(d => d.Property(x => x.Holder).Redact()).Build();

    // ── N-9: Nullable<struct> in a fluent path ─────────────────────────────────

    [Fact]
    public void AFluentPath_ThroughANullableStructsValue_ResolvesToTheStructsMember()
    {
        var policies = new KernelModelBuilder()
            .Document<DocPlainNullableStruct>(d => d.Property(x => x.Maybe!.Value.Street).Redact())
            .Build().GetRequired<DocPlainNullableStruct>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["maybe.street"]);
        Assert.False(policies.ContainsKey("maybe.value.street"));
    }

    [Fact]
    public void AFluentPolicy_ThroughANullableStruct_ReachesTheFeedDiff()
    {
        var model = new KernelModelBuilder()
            .Document<DocPlainNullableStruct>(d => d.Property(x => x.Maybe!.Value.Street).Redact())
            .Build();
        var json = JsonSerializer.SerializeToNode(
            new DocPlainNullableStruct("1", new PlainStructAddress("SECRET-STREET", "Bonn")), KernelJson.Options)!.AsObject();

        var diff = PolicyApplier.Apply(
            JsonDiffEngine.Diff(new JsonObject(), json), model.GetRequired<DocPlainNullableStruct>(), "1", hashKeys: null, ScopeContext.Tenant("tenant1"));

        Assert.DoesNotContain("SECRET-STREET", diff.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    // ── C-16: conflicting attributes ───────────────────────────────────────────

    [Theory]
    [InlineData(typeof(ConflictHashAndRedact), "[SensitiveData]", "[TrackHash]")]
    [InlineData(typeof(ConflictDoNotTrackAndHash), "[DoNotTrack]", "[TrackHash]")]
    [InlineData(typeof(ConflictReferenceAndRedact), "[TrackReference]", "[SensitiveData]")]
    [InlineData(typeof(ConflictAllFour), "[DoNotTrack]", "[SensitiveData]")]
    public void MoreThanOnePolicyAttribute_IsABuildError_NamingThePropertyAndBothAttributes(
        Type documentType, string first, string second)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildDocument(documentType));

        Assert.Contains($"{documentType.Name}.Secret", ex.Message, StringComparison.Ordinal);
        Assert.Contains(first, ex.Message, StringComparison.Ordinal);
        Assert.Contains(second, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnePolicyAttributePerProperty_IsFine()
    {
        var policies = new KernelModelBuilder().Document<SingleAttribute>().Build().GetRequired<SingleAttribute>().Policies;

        Assert.Equal(FieldPolicy.Hash, policies["secret"]);
        Assert.Equal(FieldPolicy.Redact, policies["pin"]);
    }

    [Fact]
    public void AFluentPolicy_StillOverridesAnAttribute()
    {
        var policies = new KernelModelBuilder()
            .Document<SingleAttribute>(d => d.Property(x => x.Secret).Redact())
            .Build().GetRequired<SingleAttribute>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["secret"]);
    }

    [Fact]
    public void AConflictOnAnEventProperty_IsABuildError()
    {
        // Same reader for documents and events.
        Assert.Throws<InvalidOperationException>(
            () => new KernelModelBuilder().Event<ConflictOnEvent>().Build());
    }

    public record ConflictOnEvent([property: SensitiveData, DoNotTrack] string Ip);

    // ── C-19: a member that is never serialized ────────────────────────────────

    [Fact]
    public void AFluentPolicy_OnANeverSerializedProperty_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new KernelModelBuilder().Document<DocIgnored>(d => d.Property(x => x.Hidden).Redact()));

        Assert.Equal("property", ex.ParamName);
        Assert.Contains("[JsonIgnore]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DocIgnored.Hidden", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFluentKey_OnANeverSerializedProperty_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new KernelModelBuilder().Document<DocIgnored>(d => d.UniqueKey(x => x.Hidden)));

        Assert.Equal("property", ex.ParamName);
    }

    [Fact]
    public void AFluentPolicy_OnAConditionallyIgnoredProperty_IsFine()
    {
        // WhenWritingNull only omits a null: the property is in the document whenever it has a value.
        var policies = new KernelModelBuilder()
            .Document<DocIgnored>(d => d.Property(x => x.Sometimes).Redact())
            .Build().GetRequired<DocIgnored>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["sometimes"]);
    }

    // ── C-20: event keys ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(EventWithUniqueKey))]
    [InlineData(typeof(EventWithLookupKey))]
    public void AKeyAttributeOnAnEventProperty_IsABuildError(Type eventType)
    {
        var builder = (KernelModelBuilder)typeof(KernelModelBuilder)
            .GetMethod(nameof(KernelModelBuilder.Event), 1, [])!
            .MakeGenericMethod(eventType)
            .Invoke(new KernelModelBuilder(), null)!;

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("events have no keys", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'email'", ex.Message, StringComparison.Ordinal);
    }

    private static void BuildDocument(Type documentType) =>
        ((KernelModelBuilder)typeof(KernelModelBuilder)
            .GetMethod(nameof(KernelModelBuilder.Document), 1, [])!
            .MakeGenericMethod(documentType)
            .Invoke(new KernelModelBuilder(), null)!).Build();
}
