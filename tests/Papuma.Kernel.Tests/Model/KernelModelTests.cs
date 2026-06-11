// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Tests.Model;

public sealed class KernelModelTests
{
    private sealed record Address(string City, [property: SensitiveData] string Street);

    private sealed record AttributedDoc(
        string Id,
        string Name,
        [property: SensitiveData] string Email,
        [property: TrackHash] string PasswordHash,
        [property: DoNotTrack] string? LastSeen,
        [property: UniqueKey] string CustomerNumber,
        Address? Address = null);

    private sealed record NoIdDoc(string Name);

    private sealed record CustomIdDoc(string Code, string Name);

    [Fact]
    public void AttributeScan_BuildsPolicies_WithCamelCasePaths()
    {
        var model = new KernelModelBuilder().Document<AttributedDoc>().Build();
        var metadata = model.GetRequired<AttributedDoc>();

        Assert.Equal(FieldPolicy.Redact, metadata.Policies["email"]);
        Assert.Equal(FieldPolicy.Hash, metadata.Policies["passwordHash"]);
        Assert.Equal(FieldPolicy.DoNotTrack, metadata.Policies["lastSeen"]);
        Assert.False(metadata.Policies.ContainsKey("name")); // Track is the absent default
    }

    [Fact]
    public void AttributeScan_RecursesIntoNestedPocoTypes()
    {
        var model = new KernelModelBuilder().Document<AttributedDoc>().Build();
        var metadata = model.GetRequired<AttributedDoc>();

        Assert.Equal(FieldPolicy.Redact, metadata.Policies["address.street"]);
    }

    [Fact]
    public void FluentOverride_Wins_OverAttributeDefault()
    {
        var model = new KernelModelBuilder()
            .Document<AttributedDoc>(d => d
                .Property(x => x.Email).StoreAsReference()
                .Property(x => x.LastSeen).Track())
            .Build();
        var metadata = model.GetRequired<AttributedDoc>();

        Assert.Equal(FieldPolicy.Reference, metadata.Policies["email"]);
        Assert.False(metadata.Policies.ContainsKey("lastSeen")); // explicit Track resets
    }

    [Fact]
    public void Keys_FromAttributeAndFluent_AreCollected()
    {
        var model = new KernelModelBuilder()
            .Document<AttributedDoc>(d => d.LookupKey(x => x.Name))
            .Build();
        var metadata = model.GetRequired<AttributedDoc>();

        var unique = Assert.Single(metadata.Keys, k => k.Unique);
        Assert.Equal("customerNumber", unique.Path);
        var lookup = Assert.Single(metadata.Keys, k => !k.Unique);
        Assert.Equal("name", lookup.Path);
        Assert.All(metadata.Keys, k => Assert.True(k.IndexName.Length <= 63));
    }

    [Fact]
    public void ResolvePolicy_UsesNearestAncestorPath()
    {
        var model = new KernelModelBuilder()
            .Document<AttributedDoc>(d => d.Property(x => x.Address).Redact())
            .Build();
        var metadata = model.GetRequired<AttributedDoc>();

        Assert.Equal(FieldPolicy.Redact, metadata.ResolvePolicy("address.city"));
        Assert.Equal(FieldPolicy.Track, metadata.ResolvePolicy("name"));
    }

    [Fact]
    public void Build_Throws_WhenIdConventionUnmet()
    {
        var builder = new KernelModelBuilder().Document<NoIdDoc>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("Id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HasId_DeclaresIdExplicitly()
    {
        var model = new KernelModelBuilder()
            .Document<CustomIdDoc>(d => d.HasId(x => x.Code))
            .Build();

        var id = model.GetRequired<CustomIdDoc>().GetDocumentId(new CustomIdDoc("c42", "x"));
        Assert.Equal("c42", id);
    }

    [Fact]
    public void GetRequired_Throws_ForUnregisteredType()
    {
        var model = new KernelModelBuilder().Document<AttributedDoc>().Build();

        var ex = Assert.Throws<DocumentTypeNotRegisteredException>(() => model.GetRequired<NoIdDoc>());
        Assert.Equal(typeof(NoIdDoc), ex.ClrType);
    }

    [Fact]
    public void SchemaVersion_IsHighestUpcasterFromVersionPlusOne()
    {
        var model = new KernelModelBuilder()
            .Document<AttributedDoc>(d => d
                .Upcast(1, _ => { })
                .Upcast(2, _ => { }))
            .Build();

        Assert.Equal(3, model.GetRequired<AttributedDoc>().SchemaVersion);
    }

    [Fact]
    public void SchemaVersion_DefaultsTo1_WithoutUpcasters()
    {
        var model = new KernelModelBuilder().Document<AttributedDoc>().Build();

        Assert.Equal(1, model.GetRequired<AttributedDoc>().SchemaVersion);
    }

    [Fact]
    public void Build_Throws_OnUpcasterChainGap()
    {
        var builder = new KernelModelBuilder()
            .Document<AttributedDoc>(d => d.Upcast(2, _ => { })); // missing fromVersion 1

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("gap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upcast_RejectsDuplicateFromVersion()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new KernelModelBuilder().Document<AttributedDoc>(d => d
                .Upcast(1, _ => { })
                .Upcast(1, _ => { })));
    }

    [Fact]
    public void IndexName_StaysWithinPostgresLimit_ForLongPaths()
    {
        var name = KernelModelBuilder.BuildIndexName(
            "AVeryLongDocumentTypeNameForTesting",
            "deeply.nested.property.path.that.is.quite.long.indeed",
            unique: true);

        Assert.True(name.Length <= 63);
    }
}
