// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// The one view of a property's type shared by the metamodel scan and the data inventory (review C-1, S-20).
/// </summary>
public sealed class ModelTypeShapeTests
{
    public record struct UserStruct(string A, string B);

    [JsonConverter(typeof(StructAsStringConverter))]
    public record struct ConvertedStruct(long Ticks);

    public sealed class StructAsStringConverter : JsonConverter<ConvertedStruct>
    {
        public override ConvertedStruct Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
            new(long.Parse(reader.GetString()!));

        public override void Write(System.Text.Json.Utf8JsonWriter writer, ConvertedStruct value, System.Text.Json.JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Ticks.ToString());
    }

    public sealed class UserClass
    {
        public string? A { get; set; }
    }

    public enum Color
    {
        Red,
    }

    public static TheoryData<Type, Type?> NestedCases => new()
    {
        { typeof(UserClass), typeof(UserClass) },
        { typeof(UserStruct), typeof(UserStruct) },
        { typeof(UserStruct?), typeof(UserStruct) },        // Nullable<struct> is looked through
        { typeof(string), null },
        { typeof(int), null },
        { typeof(int?), null },
        { typeof(long), null },
        { typeof(bool), null },
        { typeof(double), null },
        { typeof(decimal), null },
        { typeof(Color), null },
        { typeof(Color?), null },
        { typeof(DateTimeOffset), null },                   // framework structs are leaves
        { typeof(DateTimeOffset?), null },
        { typeof(DateTime), null },
        { typeof(TimeSpan), null },
        { typeof(Guid), null },
        { typeof(DateOnly), null },
        { typeof(ConvertedStruct), null },                  // its own converter: a leaf
        { typeof(List<UserClass>), null },                  // collections are atomic values
        { typeof(UserClass[]), null },
        { typeof(Dictionary<string, UserClass>), null },
        { typeof(JsonObject), null },
        { typeof(JsonNode), null },
    };

    [Theory]
    [MemberData(nameof(NestedCases))]
    public void NestedTypeOf_DescendsIntoObjects_AndLeavesEverythingElseALeaf(Type propertyType, Type? expected) =>
        Assert.Equal(expected, ModelTypeShape.NestedTypeOf(propertyType));

    public static TheoryData<Type, Type?> ElementCases => new()
    {
        { typeof(List<UserClass>), typeof(UserClass) },
        { typeof(UserClass[]), typeof(UserClass) },
        { typeof(IEnumerable<UserStruct>), typeof(UserStruct) },
        { typeof(IReadOnlyList<UserClass>), typeof(UserClass) },
        { typeof(HashSet<UserClass>), typeof(UserClass) },
        { typeof(Dictionary<string, UserClass>), typeof(UserClass) },            // the value, not the pair
        { typeof(IReadOnlyDictionary<string, UserStruct>), typeof(UserStruct) },
        { typeof(List<UserClass>[]), typeof(List<UserClass>) },
        { typeof(string), null },
        { typeof(int), null },
        { typeof(UserClass), null },
        { typeof(UserStruct), null },
    };

    [Theory]
    [MemberData(nameof(ElementCases))]
    public void ElementTypeOf_FindsTheElementOfAGenericCollection(Type propertyType, Type? expected) =>
        Assert.Equal(expected, ModelTypeShape.ElementTypeOf(propertyType));

    public record Annotated([property: SensitiveData] string Secret, string Open);

    public record Wrapper(Annotated Inner);

    [Fact]
    public void PolicyAttributesBeneath_ListsAnnotatedMembers_AlsoInNestedObjectsAndCollections()
    {
        Assert.Equal(["Annotated.Secret [SensitiveData]"], ModelTypeShape.PolicyAttributesBeneath(typeof(Annotated)));
        Assert.Equal(["Annotated.Secret [SensitiveData]"], ModelTypeShape.PolicyAttributesBeneath(typeof(Wrapper)));
        Assert.Equal(["Annotated.Secret [SensitiveData]"], ModelTypeShape.PolicyAttributesBeneath(typeof(List<Annotated>)));
        Assert.Empty(ModelTypeShape.PolicyAttributesBeneath(typeof(UserClass)));
        Assert.Empty(ModelTypeShape.PolicyAttributesBeneath(typeof(string)));
    }
}
