// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// Hand-picked cases for the UTF-8 prototype: where it must agree with <see cref="JsonDiffEngine"/>, where its
/// contract is deliberately stricter (empty and duplicate keys, root type), the depth limit, and that its result
/// owns its data.
/// </summary>
public sealed class Utf8JsonDiffEngineContractTests
{
    // ── Same result as the current engine ─────────────────────────────────────

    public static TheoryData<string, string, string> AgreeingCases => new()
    {
        { "null is not absent (removed)", """{"a":null}""", """{}""" },
        { "null is not absent (added)", """{}""", """{"a":null}""" },
        { "empty object gains a field", """{"a":{}}""", """{"a":{"b":1}}""" },
        { "object loses its last field", """{"a":{"b":1}}""", """{"a":{}}""" },
        { "empty array gains an element", """{"a":[]}""", """{"a":[1]}""" },
        { "array insert at the front", """{"a":[1,2,3]}""", """{"a":[0,1,2,3]}""" },
        { "array delete", """{"a":[1,2,3]}""", """{"a":[1,3]}""" },
        { "array move", """{"a":[{"id":1},{"id":2}]}""", """{"a":[{"id":2},{"id":1}]}""" },
        { "array element field change", """{"a":[{"id":1,"q":1}]}""", """{"a":[{"id":1,"q":2}]}""" },
        { "object -> array", """{"a":{"b":1}}""", """{"a":[1]}""" },
        { "object -> null", """{"a":{"b":1}}""", """{"a":null}""" },
        { "null -> object", """{"a":null}""", """{"a":{"b":1}}""" },
        { "scalar -> object", """{"a":1}""", """{"a":{"b":1}}""" },
        { "string -> number", """{"a":"1"}""", """{"a":1}""" },
        { "1 and 1.0 are equal", """{"a":1}""", """{"a":1.0}""" },
        { "100 and 1e2 are equal", """{"a":100}""", """{"a":1e2}""" },
        { "0 and -0 are equal", """{"a":0}""", """{"a":-0}""" },
        { "large integers differ by one", """{"a":9007199254740993}""", """{"a":9007199254740992}""" },
        { "0.1 and 0.10000000000000001 differ", """{"a":0.1}""", """{"a":0.10000000000000001}""" },
        { "escaped and literal string", """{"a":"\u00e9\/"}""", """{"a":"é/"}""" },
        { "escaped and literal key", """{"\u0061":1}""", """{"a":2}""" },
        { "key order is irrelevant", """{"a":1,"b":{"x":1,"y":2}}""", """{"b":{"y":2,"x":1},"a":1}""" },
        { "key order inside arrays is irrelevant", """{"a":[{"x":1,"y":2}]}""", """{"a":[{"y":2,"x":1}]}""" },
        { "whitespace is irrelevant", """{"a":{"b":[1, 2]}}""", "{ \"a\" : {\n \"b\" : [ 1 ,2 ] } }" },
        { "dot and backslash in keys", """{"a.b":{"c\\d":1}}""", """{"a.b":{"c\\d":2}}""" },
        { "dot-only keys", """{".":{"..":1}}""", """{".":{"..":2}}""" },
        { "emoji, control character, quote", """{"😀":{"k\u0001":{"q\"":1}}}""", """{"😀":{"k\u0001":{"q\"":2}}}""" },
        { "non-ASCII path", """{"日本":{"é":1}}""", """{"日本":{"é":2}}""" },
        { "unrelated documents", """{"a":1,"b":{"c":2}}""", """{"x":[1],"y":{"z":null}}""" },
        { "identical documents", """{"a":1,"b":{"c":[1,{"d":2}]}}""", """{"a":1,"b":{"c":[1,{"d":2}]}}""" },
    };

    [Theory]
    [MemberData(nameof(AgreeingCases))]
    public void AgreesWithTheCurrentEngine(string name, string before, string after)
    {
        var expected = JsonDiffEngine.Diff(Parse(before), Parse(after));
        var actual = Utf8JsonDiffEngine.Diff(before, after);

        Assert.True(DiffComparison.Difference(expected, actual.ToDocumentDiff()) is null, name);
        Assert.True(DiffComparison.Difference(expected, DiffComparison.FromWire(actual.ToUtf8Json())) is null, name);
        Assert.Null(DiffComparison.Reconstruction(Parse(before), Parse(after), actual.ToDocumentDiff()));
    }

    [Fact]
    public void AnAbsentDocument_IsTheEmptyObject()
    {
        const string document = """{"name":"Harry","address":{"city":"Bonn"},"tags":[1]}""";

        var insert = Utf8JsonDiffEngine.Diff(null, document);
        var delete = Utf8JsonDiffEngine.Diff(document, null);

        Assert.Null(DiffComparison.Difference(Utf8JsonDiffEngine.Diff("{}", document).ToDocumentDiff(), insert.ToDocumentDiff()));
        Assert.Null(DiffComparison.Difference(Utf8JsonDiffEngine.Diff(document, "{}").ToDocumentDiff(), delete.ToDocumentDiff()));
        Assert.Equal(new[] { "address", "name", "tags" }, insert.Entries.Select(e => e.Path)); // top level, whole values
        Assert.All(insert.Entries, e => Assert.False(e.HasOld));
        Assert.All(delete.Entries, e => Assert.False(e.HasNew));
        Assert.True(Utf8JsonDiffEngine.Diff((string?)null, null).IsEmpty);
    }

    // ── Empty keys are ordinary keys (an empty path segment) ──────────────────

    [Theory]
    [InlineData("""{"":1}""", """{"":2}""", "")]
    [InlineData("""{"":1,"b":1}""", """{"":1,"b":2}""", "b")]
    [InlineData("""{"x":{"":1},"b":1}""", """{"x":{"":2},"b":1}""", "x.")]
    [InlineData("""{"":{"":1}}""", """{"":{"":2}}""", ".")]
    [InlineData("""{"":{"a":1}}""", """{"":{"a":2}}""", ".a")]
    [InlineData("""{"a":[{"":1}]}""", """{"a":[{"":2}]}""", "a")] // inside an array: atomic
    [InlineData(null, """{"":{"":1}}""", "")] // insert: one top-level entry
    [InlineData("""{"x":{"":1}}""", null, "x")] // delete
    public void AnEmptyKey_IsAnEmptySegment_AndBothEnginesAgree(string? before, string? after, string path)
    {
        var expected = JsonDiffEngine.Diff(Parse(before), Parse(after));

        var actual = Utf8JsonDiffEngine.Diff(before, after);

        Assert.Equal([path], actual.Entries.Select(e => e.Path));
        Assert.Null(DiffComparison.Difference(expected, actual.ToDocumentDiff()));
        Assert.Null(DiffComparison.Difference(expected, DiffComparison.DiffUtf8(DiffComparison.Utf8(before), DiffComparison.Utf8(after)).ToDocumentDiff()));
        Assert.Null(DiffComparison.Reconstruction(Parse(before), Parse(after), actual.ToDocumentDiff()));
    }

    // ── Deliberately stricter than the current engine ─────────────────────────

    [Theory]
    [InlineData("""{"a":1,"a":2}""", """{"a":2}""")]
    [InlineData("""{"a":2}""", """{"a":1,"a":3}""")]
    [InlineData("""{"x":{"a":1,"a":1}}""", """{"x":{"a":1,"a":1}}""")] // identical, and nested
    [InlineData("""{"\u0061":1,"a":2}""", """{}""")] // a duplicate spelled differently
    public void ADuplicateKey_IsRejected(string before, string after)
    {
        Assert.ThrowsAny<JsonException>(() => Utf8JsonDiffEngine.Diff(before, after));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void ARootThatIsNotAnObject_IsRejected_NamingTheSide(string notAnObject)
    {
        Assert.Equal("before", Assert.Throws<ArgumentException>(() => Utf8JsonDiffEngine.Diff(notAnObject, "{}")).ParamName);
        Assert.Equal("after", Assert.Throws<ArgumentException>(() => Utf8JsonDiffEngine.Diff("{}", notAnObject)).ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"a":}""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":1} {"b":2}""")]
    public void MalformedJson_IsRejected(string malformed)
    {
        Assert.ThrowsAny<JsonException>(() => Utf8JsonDiffEngine.Diff(malformed, "{}"));
    }

    // ── Depth ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ADocumentAtTheDepthLimit_IsDiffed_OneLevelMore_IsRejected()
    {
        var atLimit = Nested(JsonLimits.MaxDepth, "1");
        var changed = Nested(JsonLimits.MaxDepth, "2");

        var diff = Utf8JsonDiffEngine.Diff(atLimit, changed);

        Assert.Single(diff.Entries);
        Assert.Equal(JsonLimits.MaxDepth, diff.Entries[0].Path.Split('.').Length);
        Assert.ThrowsAny<JsonException>(() => Utf8JsonDiffEngine.Diff(Nested(JsonLimits.MaxDepth + 1, "1"), "{}"));
        Assert.ThrowsAny<JsonException>(() => Utf8JsonDiffEngine.Diff(Nested(10_000, "1"), "{}"));
    }

    [Fact]
    public void NestingInsideArrays_CountsTowardsTheLimit()
    {
        var arrays = new string('[', JsonLimits.MaxDepth) + new string(']', JsonLimits.MaxDepth);

        Assert.ThrowsAny<JsonException>(() => Utf8JsonDiffEngine.Diff($$"""{"a":{{arrays}}}""", "{}"));
    }

    // One limit everywhere: what the sessions can serialize and parse is exactly what the engines accept.
    [Fact]
    public void TheEngineLimit_IsTheParsersAndTheSerializersLimit()
    {
        Assert.Equal(64, JsonLimits.MaxDepth);
        Assert.NotNull(JsonNode.Parse(Nested(JsonLimits.MaxDepth, "1"))); // sessions: JsonNode.Parse, default options
        Assert.ThrowsAny<JsonException>(() => JsonNode.Parse(Nested(JsonLimits.MaxDepth + 1, "1")));
        Assert.Equal(JsonLimits.MaxDepth, Papuma.Kernel.Model.KernelJson.Options.MaxDepth);
        Assert.False(JsonLimits.ExceedsMaxDepth(JsonNode.Parse(Nested(JsonLimits.MaxDepth, "1"))));
    }

    // ── The result owns its data ──────────────────────────────────────────────

    [Fact]
    public void TheResult_SurvivesItsInputBuffersBeingOverwritten()
    {
        var before = Encoding.UTF8.GetBytes("""{"name":"Harry","address":{"city":"Bonn","zip":"53111"},"tags":["a"]}""");
        var after = Encoding.UTF8.GetBytes("""{"name":"Harald","address":{"city":"Köln","zip":"53111"},"tags":["a","b"]}""");
        var expectedWire = Utf8JsonDiffEngine.Diff(before.ToArray().AsMemory(), after.ToArray().AsMemory()).ToUtf8Json();

        var diff = Utf8JsonDiffEngine.Diff(before.AsMemory(), after.AsMemory());
        before.AsSpan().Fill((byte)'x');
        after.AsSpan().Clear();

        Assert.Equal(expectedWire, diff.ToUtf8Json());
        var materialized = diff.ToDocumentDiff();
        Assert.Equal("Köln", (string?)materialized.Entries["address.city"].New);
        Assert.Null(DiffComparison.Reconstruction(
            Parse("""{"name":"Harry","address":{"city":"Bonn","zip":"53111"},"tags":["a"]}"""),
            Parse("""{"name":"Harald","address":{"city":"Köln","zip":"53111"},"tags":["a","b"]}"""),
            materialized));
    }

    [Fact]
    public void TheResult_DoesNotKeepItsInputsAlive()
    {
        var (beforeInput, afterInput, diff) = DiffOfCollectableInputs();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(beforeInput.IsAlive);
        Assert.False(afterInput.IsAlive);
        Assert.Equal("2", Encoding.UTF8.GetString(diff.NewValue(diff.Entries.Single())));
    }

    [Fact]
    public void TheResult_IsIndependentOfTheParsedDocuments()
    {
        var before = Utf8JsonDiffEngine.Parse("""{"a":{"b":1},"c":"x"}""", "before");
        var after = Utf8JsonDiffEngine.Parse("""{"a":{"b":2},"c":"x"}""", "after");

        var diff = Utf8JsonDiffEngine.Diff(before, after);
        before.Dispose();
        after.Dispose();

        Assert.Equal("""{"a.b":{"old":1,"new":2}}""", Encoding.UTF8.GetString(diff.ToUtf8Json()));
    }

    [Fact]
    public void TheResult_HoldsOnlyTheChangedValues()
    {
        var before = new JsonObject();
        for (var i = 0; i < 5_000; i++)
        {
            before[$"field{i}"] = $"value-{i}";
        }

        var after = (JsonObject)before.DeepClone();
        after["field42"] = "changed";

        var beforeText = before.ToJsonString();
        var diff = Utf8JsonDiffEngine.Diff(beforeText, after.ToJsonString());

        Assert.Equal("\"value-42\"\"changed\"".Length, diff.ValueBytes);
        Assert.True(diff.ValueBytes < beforeText.Length / 1000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Before, WeakReference After, Utf8Diff Diff) DiffOfCollectableInputs()
    {
        var before = Encoding.UTF8.GetBytes("""{"a":1,"padding":"................................................"}""");
        var after = Encoding.UTF8.GetBytes("""{"a":2,"padding":"................................................"}""");
        return (new WeakReference(before), new WeakReference(after), Utf8JsonDiffEngine.Diff(before.AsMemory(), after.AsMemory()));
    }

    private static string Nested(int levels, string leaf)
    {
        var builder = new StringBuilder();
        for (var i = 1; i < levels; i++)
        {
            builder.Append("{\"n\":");
        }

        builder.Append("{\"v\":").Append(leaf).Append('}');
        builder.Append('}', levels - 1);
        return builder.ToString();
    }

    private static JsonObject? Parse(string? text) =>
        text is null ? null : (JsonObject)JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = JsonLimits.MaxDepth })!;
}
