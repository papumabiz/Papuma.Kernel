// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// The model must refuse what would silently misbehave (review C-8/N-5, C-5, C-9, S-12): two declared keys that
/// share one index name — the second index was never created, so its UNIQUE constraint was never enforced — an id
/// expression that is not a property of the document, an upcast outside the version range, and key path segments
/// that the DDL validator wrongly rejected.
/// </summary>
public sealed class ModelIntegrityTests
{
    // ── Types ──────────────────────────────────────────────────────────────────

    public record Inner([property: UniqueKey] string B);

    public record DotVersusUnderscore(string Id, [property: UniqueKey] string a_b, Inner A);

    public record CaseOnly(
        string Id,
        [property: UniqueKey, JsonPropertyName("Value")] string One,
        [property: UniqueKey, JsonPropertyName("value")] string Two);

    public record OrderItem(string Id, [property: UniqueKey] string x);

    public record Order(string Id, [property: UniqueKey] string item_x);

    public record UniqueAndLookup(string Id, [property: UniqueKey] string Email, [property: LookupKey] string Group);

    public record SameKeyOtherType(string Id, [property: UniqueKey] string Email);

    public record CompositeCollision(string Id, string a, string b, string a__b);

    public record DocNested(string Id, NestedId Nested, string Name, string Code);

    public record NestedId(string Id);

    public record WithField(string Id)
    {
        public string Code = "c";
    }

    // ── C-8 / N-5 ──────────────────────────────────────────────────────────────

    [Fact]
    public void TwoKeysInOneType_WithTheSameIndexName_AreABuildError()
    {
        // a.b and a_b both become "ux_papuma_doc_dotversusunderscore_a_b".
        var ex = Assert.Throws<InvalidOperationException>(
            () => new KernelModelBuilder().Document<DotVersusUnderscore>().Build());

        Assert.Contains("a_b", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a.b", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ux_papuma_doc_dotversusunderscore_a_b", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DotVersusUnderscore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoKeysThatDifferOnlyInCase_AreABuildError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new KernelModelBuilder().Document<CaseOnly>().Build());

        Assert.Contains("Value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeysOfDifferentTypes_WithTheSameIndexName_AreABuildError()
    {
        // Order_Item + x and Order + item_x both become "ux_papuma_doc_order_item_x".
        var builder = new KernelModelBuilder()
            .Document<OrderItem>(d => d.Named("Order_Item"))
            .Document<Order>(d => d.Named("Order"));

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("Order_Item", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Order", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ux_papuma_doc_order_item_x", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameKeyPath_InDifferentTypes_IsFine_BecauseTheTypeNameIsPartOfTheIndexName() =>
        new KernelModelBuilder().Document<UniqueAndLookup>().Document<SameKeyOtherType>().Build();

    [Fact]
    public void AUniqueAndALookupKey_AreDifferentIndexes() =>
        new KernelModelBuilder().Document<UniqueAndLookup>().Build();

    [Fact]
    public void ACompositeKey_AndAPropertyThatSpellsTheSameIndexName_AreABuildError()
    {
        // (a, b) joins its components with "__" so that it cannot collide with the path a.b — but a property
        // that is literally called a__b still spells the same name.
        var ex = Assert.Throws<InvalidOperationException>(() => new KernelModelBuilder()
            .Document<CompositeCollision>(d => d.UniqueKey(x => new { x.a, x.b }).UniqueKey(x => x.a__b))
            .Build());

        Assert.Contains("a__b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompositeKey_AndTheSamePropertiesAsALookupKey_AreDifferentIndexes() =>
        new KernelModelBuilder()
            .Document<CompositeCollision>(d => d.UniqueKey(x => new { x.a, x.b }).LookupKey(x => new { x.a, x.b }))
            .Build();

    // ── C-5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void HasId_OnAPropertyOfTheDocument_IsAccepted()
    {
        var model = new KernelModelBuilder().Document<DocNested>(d => d.HasId(x => x.Code)).Build();

        Assert.Equal("code", model.GetRequired<DocNested>().IdPath);
    }

    [Fact]
    public void WithoutHasId_TheConventionalIdProperty_IsTheIdPath()
    {
        var model = new KernelModelBuilder().Document<DocNested>().Build();

        Assert.Equal("id", model.GetRequired<DocNested>().IdPath);
    }

    public static TheoryData<string> BadIdExpressions => new() { "nested", "method", "field", "constant" };

    [Theory]
    [MemberData(nameof(BadIdExpressions))]
    public void HasId_OnAnythingButAPropertyOfTheDocument_IsRefusedAtTheCall(string kind)
    {
        Action configure = kind switch
        {
            // x.Nested.Id is the property of the NESTED type: GetValue(document) would throw at the first save.
            "nested" => () => new KernelModelBuilder().Document<DocNested>(d => d.HasId(x => x.Nested.Id)),
            // x.Name.Trim() is not a member access: it used to fail with an InvalidCastException in Build().
            "method" => () => new KernelModelBuilder().Document<DocNested>(d => d.HasId(x => x.Name.Trim())),
            "field" => () => new KernelModelBuilder().Document<WithField>(d => d.HasId(x => x.Code)),
            _ => () => new KernelModelBuilder().Document<DocNested>(d => d.HasId(x => "constant")),
        };

        var ex = Assert.Throws<ArgumentException>(configure);

        Assert.Equal("id", ex.ParamName);
    }

    // ── C-9 ────────────────────────────────────────────────────────────────────

    public record Versioned(string Id, string Name);

    private static DocumentTypeMetadata VersionedMetadata() =>
        new KernelModelBuilder()
            .Document<Versioned>(d => d
                .Upcast(1, json => json["step1"] = true)
                .Upcast(2, json => json["step2"] = true))
            .Build().GetRequired<Versioned>();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Upcast_OutsideTheVersionRange_IsRefused(int storedSchemaVersion)
    {
        var metadata = VersionedMetadata();
        Assert.Equal(3, metadata.SchemaVersion);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => metadata.Upcast(new JsonObject(), storedSchemaVersion));

        Assert.Equal("storedSchemaVersion", ex.ParamName);
    }

    [Fact]
    public void Upcast_FromTheCurrentVersion_IsANoOp()
    {
        var json = new JsonObject();

        VersionedMetadata().Upcast(json, 3);

        Assert.Empty(json);
    }

    [Fact]
    public void Upcast_FromAnOlderVersion_RunsTheChainFromThere()
    {
        var json = new JsonObject();

        VersionedMetadata().Upcast(json, 2);

        Assert.False(json.ContainsKey("step1"));
        Assert.True((bool)json["step2"]!);
    }

    // ── S-12: path segments ────────────────────────────────────────────────────

    [Theory]
    [InlineData("x")]
    [InlineData("X")]
    [InlineData("_id")]
    [InlineData("_")]
    [InlineData("a1")]
    [InlineData("a_b")]
    [InlineData("email")]
    [InlineData("camelCaseName")]
    public void ValidateJsonPathSegment_AcceptsWhatAPropertyIsCalled(string segment) =>
        InputValidator.ValidateJsonPathSegment(segment);

    [Fact]
    public void ValidateJsonPathSegment_AcceptsTheLongestSegment() =>
        InputValidator.ValidateJsonPathSegment(new string('a', InputValidator.MaxJsonPathSegmentLength));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1a")]
    [InlineData("a-b")]
    [InlineData("a.b")]
    [InlineData("a b")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a,b")]
    [InlineData("a{b")]
    [InlineData("a\n")]
    [InlineData("ä")]
    public void ValidateJsonPathSegment_RejectsAnythingThatCouldReachTheSql_BlamingSegment(string? segment)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => InputValidator.ValidateJsonPathSegment(segment!));

        Assert.Equal("segment", ex.ParamName);
    }

    [Fact]
    public void ValidateJsonPathSegment_RejectsATooLongSegment() =>
        Assert.Throws<ArgumentException>(
            () => InputValidator.ValidateJsonPathSegment(new string('a', InputValidator.MaxJsonPathSegmentLength + 1)));
}
