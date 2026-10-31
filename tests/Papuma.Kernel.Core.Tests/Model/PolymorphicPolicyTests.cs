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
/// Policies on types the declared property type does not show (review N-14, N-15, decision D-9): the members of
/// <c>[JsonDerivedType]</c> subtypes are scanned under the property's path, an <c>object</c> property needs an explicit
/// decision, and an interface or abstract property type without derived types — which the serializer writes with the
/// declared members only and cannot read back — is a build error.
/// </summary>
public sealed class PolymorphicPolicyTests
{
    // ── Types ──────────────────────────────────────────────────────────────────

    [JsonDerivedType(typeof(Card), "card")]
    [JsonDerivedType(typeof(Transfer), "transfer")]
    public abstract record Payment(decimal Amount);

    public sealed record Card(decimal Amount, [property: SensitiveData] string Pan) : Payment(Amount);

    public sealed record Transfer(decimal Amount, [property: DoNotTrack] string Iban) : Payment(Amount);

    public record DocPayment(string Id, Payment Payment);

    [JsonDerivedType(typeof(EmailContact), "email")]
    public interface IContact;

    public sealed record EmailContact([property: SensitiveData] string Email) : IContact;

    public record DocContact(string Id, IContact Contact);

    [JsonDerivedType(typeof(Coupon), "coupon")]
    [JsonDerivedType(typeof(GiftCard), "gift")]
    public abstract record Voucher;

    public sealed record Coupon([property: SensitiveData] string Code) : Voucher;

    public sealed record GiftCard([property: DoNotTrack] string Code) : Voucher;

    public record DocConflict(string Id, Voucher Voucher);

    [JsonDerivedType(typeof(SameA), "a")]
    [JsonDerivedType(typeof(SameB), "b")]
    public abstract record Same;

    public sealed record SameA([property: SensitiveData] string Code) : Same;

    public sealed record SameB([property: SensitiveData] string Code, int Extra) : Same;

    public record DocSamePolicy(string Id, Same Same);

    public record DocPaymentList(string Id, List<Payment> Payments);

    public record DocObject(string Id, object? Payload);

    public record DocAnnotatedObject(string Id, [property: SensitiveData] object? Payload);

    public record Envelope(string Kind, object? Payload);

    public record DocNestedObject(string Id, Envelope Envelope);

    public record DocObjectList(string Id, List<object> Items);

    public record DocObjectDictionary(string Id, Dictionary<string, object> Attributes);

    public record DocEnvelopeList(string Id, List<Envelope> Envelopes);

    public record DocFreeJson(string Id, JsonNode? Node, JsonObject? Bag, JsonElement Element, Dictionary<string, JsonNode?> Extra);

    public record EventObject(string UserId, object? Payload);

    public interface IPlainContact
    {
        string Name { get; }
    }

    public sealed record PlainContact(string Name, string Phone) : IPlainContact;

    public record DocInterface(string Id, IPlainContact Contact);

    public abstract record Shape(string Name);

    public sealed record Circle(string Name, double Radius) : Shape(Name);

    public record DocAbstract(string Id, Shape Shape);

    public record DocInterfaceList(string Id, List<IPlainContact> Contacts);

    public record DocInterfaceInElement(string Id, List<Holder> Holders);

    public record Holder(IPlainContact Contact);

    [JsonConverter(typeof(ConvertedContactConverter))]
    public interface IConvertedContact
    {
        string Name { get; }
    }

    public sealed record ConvertedContact(string Name) : IConvertedContact;

    public sealed class ConvertedContactConverter : JsonConverter<IConvertedContact>
    {
        public override IConvertedContact Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new ConvertedContact(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, IConvertedContact value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Name);
    }

    public record DocConvertedInterface(string Id, IConvertedContact Contact);

    public record EventInterface(string UserId, IPlainContact Contact);

    public record Party(string Name);

    public sealed record Person(string Name, string Ssn) : Party(Name);

    public record DocConcreteBase(string Id, Party Party);

    // ── D-9 (1): [JsonDerivedType] subtypes are scanned ────────────────────────

    [Fact]
    public void AMemberOfADerivedType_GetsItsPolicy_UnderThePropertysPath()
    {
        var policies = new KernelModelBuilder().Document<DocPayment>().Build().GetRequired<DocPayment>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["payment.pan"]);
        Assert.Equal(FieldPolicy.DoNotTrack, policies["payment.iban"]);
    }

    [Fact]
    public void AMemberOfAnInterfacesDerivedType_GetsItsPolicy()
    {
        var policies = new KernelModelBuilder().Document<DocContact>().Build().GetRequired<DocContact>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["contact.email"]);
    }

    [Fact]
    public void ADerivedTypesSecret_DoesNotReachTheFeedDiagnosticDiff()
    {
        var model = new KernelModelBuilder().Document<DocPayment>().Build();
        var json = JsonSerializer.SerializeToNode(new DocPayment("1", new Card(12.5m, "SECRET-PAN")), KernelJson.Options)!.AsObject();

        var diff = PolicyApplier.Apply(
            JsonDiffEngine.Diff(new JsonObject(), json), model.GetRequired<DocPayment>(), "1", hashKeys: null, ScopeContext.Tenant("tenant1"));

        var feed = diff.ToJson().ToJsonString();
        Assert.DoesNotContain("SECRET-PAN", feed, StringComparison.Ordinal);
        Assert.Contains("12.5", feed, StringComparison.Ordinal); // the rest stays tracked
    }

    [Fact]
    public void ADerivedType_ReadsBack_WhenTheDiscriminatorIsNotTheFirstProperty()
    {
        // PostgreSQL's jsonb reorders object keys (shorter first): "$type" comes back after "pan" and "amount".
        var json = """{"id":"1","payment":{"pan":"4111","amount":10,"$type":"card"}}""";

        var document = JsonSerializer.Deserialize<DocPayment>(json, KernelJson.Options)!;

        Assert.Equal(new Card(10m, "4111"), document.Payment);
    }

    [Fact]
    public void TwoDerivedTypes_WithDifferentPoliciesOnOnePath_AreABuildError_NamingBothTypes()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KernelModelBuilder().Document<DocConflict>().Build());

        Assert.Contains("'voucher.code'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Coupon), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(GiftCard), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDerivedTypes_WithTheSamePolicyOnOnePath_AreFine()
    {
        var policies = new KernelModelBuilder().Document<DocSamePolicy>().Build().GetRequired<DocSamePolicy>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["same.code"]);
    }

    [Fact]
    public void ACollectionOfPolymorphicElements_WithAPolicyInADerivedType_IsABuildError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KernelModelBuilder().Document<DocPaymentList>().Build());

        Assert.Contains("'payments'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Card.Pan [SensitiveData]", ex.Message, StringComparison.Ordinal);
    }

    // ── D-9 (2): object needs a decision ───────────────────────────────────────

    [Theory]
    [InlineData(typeof(DocObject), "payload")]
    [InlineData(typeof(DocNestedObject), "envelope.payload")]
    [InlineData(typeof(DocObjectList), "items")]
    [InlineData(typeof(DocObjectDictionary), "attributes")]
    [InlineData(typeof(DocEnvelopeList), "envelopes")]
    public void AnUntypedProperty_WithoutADecision_IsABuildError_NamingThePathAndTheWayOut(Type documentType, string path)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildDocument(documentType));

        Assert.Contains($"'{path}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("object", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".Track()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntypedProperty_DecidedByAnAttribute_IsFine()
    {
        var policies = new KernelModelBuilder().Document<DocAnnotatedObject>().Build().GetRequired<DocAnnotatedObject>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["payload"]);
    }

    [Fact]
    public void AnUntypedProperty_DecidedByAFluentPolicy_IsFine()
    {
        var policies = new KernelModelBuilder()
            .Document<DocObject>(d => d.Property(x => x.Payload).Redact())
            .Build().GetRequired<DocObject>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["payload"]);
    }

    [Fact]
    public void AnUntypedProperty_ExplicitlyTracked_IsAnAcknowledgement()
    {
        var model = new KernelModelBuilder().Document<DocObject>(d => d.Property(x => x.Payload).Track()).Build();

        Assert.False(model.GetRequired<DocObject>().Policies.ContainsKey("payload"));
    }

    [Fact]
    public void AnUntypedProperty_BeneathADecidedAncestor_IsDecided() =>
        new KernelModelBuilder().Document<DocNestedObject>(d => d.Property(x => x.Envelope).Track()).Build();

    [Fact]
    public void AnUntypedCollection_DecidedOnTheCollection_IsFine()
    {
        new KernelModelBuilder().Document<DocObjectList>(d => d.Property(x => x.Items).Track()).Build();
        new KernelModelBuilder().Document<DocObjectDictionary>(d => d.Property(x => x.Attributes).Redact()).Build();
    }

    [Fact]
    public void AnEventsUntypedProperty_WithoutADecision_IsABuildError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KernelModelBuilder().Event<EventObject>().Build());

        Assert.Contains("Event type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'payload'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeFormJsonProperties_NeedNoDecision()
    {
        // JsonNode, JsonObject and JsonElement say "free-form" by their type; they carry no attributes of ours, and a
        // fluent policy on the property is the way to protect them (D-9 (3)).
        var model = new KernelModelBuilder().Document<DocFreeJson>().Build();

        Assert.Empty(model.GetRequired<DocFreeJson>().Policies);
    }

    // ── D-9 (4) / N-15: a property type the serializer cannot round-trip ───────

    [Theory]
    [InlineData(typeof(DocInterface), "contact", nameof(IPlainContact))]
    [InlineData(typeof(DocAbstract), "shape", nameof(Shape))]
    [InlineData(typeof(DocInterfaceList), "contacts", nameof(IPlainContact))]
    [InlineData(typeof(DocInterfaceInElement), "holders", nameof(IPlainContact))]
    public void AnInterfaceOrAbstractPropertyType_WithoutDerivedTypes_IsABuildError(Type documentType, string path, string typeName)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildDocument(documentType));

        Assert.Contains($"'{path}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[JsonDerivedType", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventsInterfaceProperty_WithoutDerivedTypes_IsABuildError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KernelModelBuilder().Event<EventInterface>().Build());

        Assert.Contains("Event type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'contact'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterfaceWithItsOwnConverter_IsFine() =>
        new KernelModelBuilder().Document<DocConvertedInterface>().Build();

    [Fact]
    public void WhatTheRuleGuardsAgainst_TheSerializerWritesOnlyTheDeclaredMembers()
    {
        // Pinned so a change in System.Text.Json is noticed: an interface property keeps only the interface's members,
        // and a concrete base class drops the subtype's — the second case cannot be seen at build time (any unsealed
        // class could be subclassed), so it stays documented, not checked (docs/ai/papuma-kernel-playbook.md).
        var viaInterface = JsonSerializer.SerializeToNode(new DocInterface("1", new PlainContact("Ann", "SECRET-PHONE")), KernelJson.Options)!;
        var viaBase = JsonSerializer.SerializeToNode(new DocConcreteBase("1", new Person("Ann", "SECRET-SSN")), KernelJson.Options)!;

        Assert.Equal("""{"name":"Ann"}""", viaInterface["contact"]!.ToJsonString());
        Assert.Equal("""{"name":"Ann"}""", viaBase["party"]!.ToJsonString());
        new KernelModelBuilder().Document<DocConcreteBase>().Build(); // a concrete base class builds
    }

    private static void BuildDocument(Type documentType) =>
        ((KernelModelBuilder)typeof(KernelModelBuilder)
            .GetMethod(nameof(KernelModelBuilder.Document), 1, [])!
            .MakeGenericMethod(documentType)
            .Invoke(new KernelModelBuilder(), null)!).Build();
}
