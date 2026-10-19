// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// The logical type name is declared, not derived from the class (ADR-028): by attribute or fluent
/// <c>Named</c>, defaulting to the simple class name. Renaming or moving a class must not change where
/// its documents are, which key their hashes use, or what their key indexes are called.
/// </summary>
public sealed class ExplicitTypeNameTests
{
    [DocumentType("customer")]
    public sealed record CustomerV1(string Id, [property: UniqueKey] string Email, [property: TrackReference] string Iban);

    // The same documents after a refactoring: another class name, the same declared name.
    [DocumentType("customer")]
    public sealed record ClientV2(string Id, [property: UniqueKey] string Email, [property: TrackReference] string Iban);

    [DocumentType("from_attribute")]
    public sealed record Attributed(string Id);

    public sealed record Plain(string Id);

    [EventType("user_signed_in")]
    public sealed record SignedIn(string UserId);

    public static class SalesArea
    {
        public sealed record Item(string Id, string Name);
    }

    public static class StockArea
    {
        [DocumentType("StockItem")]
        public sealed record Item(string Id, int Count);
    }

    private static string DocumentNameOf<T>(Action<KernelModelBuilder.DocumentTypeBuilder<T>>? configure = null)
        where T : class =>
        new KernelModelBuilder().Document(configure ?? (_ => { })).Build().GetRequired<T>().Name;

    [Fact]
    public void WithoutADeclaration_TheNameIsTheSimpleClassName()
    {
        Assert.Equal("Plain", DocumentNameOf<Plain>());
    }

    [Fact]
    public void TheAttribute_DeclaresTheName_ForDocumentsAndEvents()
    {
        var model = new KernelModelBuilder().Document<Attributed>().Event<SignedIn>().Build();

        Assert.Equal("from_attribute", model.GetRequired<Attributed>().Name);
        Assert.Equal("user_signed_in", model.GetRequiredEvent<SignedIn>().Name);
    }

    [Fact]
    public void FluentNamed_OverridesTheAttribute()
    {
        Assert.Equal("fluent_wins", DocumentNameOf<Attributed>(d => d.Named("fluent_wins")));

        var model = new KernelModelBuilder().Event<SignedIn>(e => e.Named("fluent_event")).Build();
        Assert.Equal("fluent_event", model.GetRequiredEvent<SignedIn>().Name);
    }

    [Theory]
    [InlineData("user-account")]
    [InlineData("1abc")]
    [InlineData("a")]
    [InlineData("with space")]
    [InlineData("quote'")]
    [InlineData("")]
    public void AnInvalidName_IsRejected_ByNamed(string name)
    {
        Assert.Throws<ArgumentException>(() => new KernelModelBuilder().Document<Plain>(d => d.Named(name)));
    }

    [Fact]
    public void AnInvalidName_IsRejected_WhenItComesFromTheAttribute()
    {
        Assert.Throws<ArgumentException>(() => new KernelModelBuilder().Document<BadlyNamed>().Build());
    }

    [DocumentType("not valid!")]
    private sealed record BadlyNamed(string Id);

    [Fact]
    public void ADeclaredName_ResolvesACollisionOfSimpleNames()
    {
        var model = new KernelModelBuilder().Document<SalesArea.Item>().Document<StockArea.Item>().Build();

        Assert.Equal(["Item", "StockItem"], model.DocumentTypes.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TwoTypesDeclaringTheSameName_AreRejected_AndTheMessageSaysHowToFixIt()
    {
        var builder = new KernelModelBuilder().Document<CustomerV1>().Document<ClientV2>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("'customer'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[DocumentType(", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".Named(", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARenamedClass_KeepsEveryDerivedName()
    {
        var before = new KernelModelBuilder().Document<CustomerV1>().Build().GetRequired<CustomerV1>();
        var after = new KernelModelBuilder().Document<ClientV2>().Build().GetRequired<ClientV2>();

        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Keys[0].IndexName, after.Keys[0].IndexName);
        Assert.Contains("customer", after.Keys[0].IndexName, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceText_UsesTheDeclaredName()
    {
        var metadata = new KernelModelBuilder().Document<ClientV2>().Build().GetRequired<ClientV2>();
        var diff = JsonDiffEngine.Diff(
            (JsonObject)JsonNode.Parse("""{"iban":"a"}""")!,
            (JsonObject)JsonNode.Parse("""{"iban":"b"}""")!);

        var applied = PolicyApplier.Apply(diff, metadata, "42", hashKeys: null, ScopeContext.Tenant("acme"));

        Assert.Equal("customer/42/iban", applied.Entries["iban"].Reference);
    }

    [Fact]
    public void AHashSurvivesRenamingTheClass_BecauseTheKeyIsDerivedFromTheDeclaredName()
    {
        var provider = new MasterKeyHashKeyProvider(
            Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"), "k1");
        var scope = ScopeContext.Tenant("acme");

        var oldKey = provider.GetKey(scope, new KernelModelBuilder().Document<CustomerV1>().Build().GetRequired<CustomerV1>().Name);
        var newKey = provider.GetKey(scope, new KernelModelBuilder().Document<ClientV2>().Build().GetRequired<ClientV2>().Name);

        Assert.Equal(oldKey.ToArray(), newKey.ToArray());
    }
}
