// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// <c>JsonValue</c>s built by hand — <c>JsonValue.Create(guid)</c>, <c>JsonValue.Create(poco)</c> — are
/// canonicalized by what they serialize to, never silently as <c>null</c> (review C-6).
/// </summary>
public sealed class CanonicalJsonValueTests
{
    private sealed record Poco(string Name, int Count);

    private static readonly JsonTypeInfo<Poco> PocoInfo =
        (JsonTypeInfo<Poco>)new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        }.GetTypeInfo(typeof(Poco));

    [Fact]
    public void AGuidValue_IsCanonicalizedAsItsString()
    {
        var guid = Guid.Parse("5b2a3c1e-0f4d-4c1b-9a5e-1f2e3d4c5b6a");

        Assert.Equal("\"5b2a3c1e-0f4d-4c1b-9a5e-1f2e3d4c5b6a\"", CanonicalJson.Serialize(JsonValue.Create(guid)));
    }

    [Fact]
    public void ADateTimeOffsetValue_IsCanonicalizedAsItsString()
    {
        var value = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("\"2026-10-20T12:00:00+00:00\"", CanonicalJson.Serialize(JsonValue.Create(value)));
    }

    [Fact]
    public void APocoValue_IsCanonicalizedAsTheObjectItSerializesTo_NotAsNull()
    {
        var node = JsonValue.Create(new Poco("x", 2), PocoInfo);

        Assert.Equal("""{"count":2,"name":"x"}""", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void ThePocoValue_KeepsItsKeysSorted_InsideAnObject()
    {
        var root = new JsonObject
        {
            ["z"] = 1,
            ["a"] = JsonValue.Create(new Poco("x", 2), PocoInfo),
        };

        var text = CanonicalJson.Serialize(root);

        Assert.StartsWith("{\"a\":{", text, StringComparison.Ordinal);
        Assert.EndsWith(",\"z\":1}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("null", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueWithNoJsonKind_IsRejected_NotCanonicalizedAsNull()
    {
        // A default JsonElement has ValueKind Undefined: it has no JSON form at all.
        var node = JsonValue.Create(default(JsonElement));

        // JsonValue.Create(default(JsonElement)) yields a node whose kind is Undefined.
        var ex = Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Serialize(node));

        Assert.Equal("invalid-value", ex.Code);
    }

    [Fact]
    public void OrdinaryNodes_StillSerializeAsBefore()
    {
        var node = JsonNode.Parse("""{"b":[1,2.50,"s",true,null],"a":{"y":false}}""");

        Assert.Equal("""{"a":{"y":false},"b":[1,2.5,"s",true,null]}""", CanonicalJson.Serialize(node));
        Assert.Equal("\"abc\"", CanonicalJson.Serialize(JsonValue.Create("abc")));
        Assert.Equal("12", CanonicalJson.Serialize(JsonValue.Create(12)));
    }
}
