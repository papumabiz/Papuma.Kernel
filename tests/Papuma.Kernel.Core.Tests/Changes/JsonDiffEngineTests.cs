// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Core.Tests.Changes;

public sealed class JsonDiffEngineTests
{
    [Fact]
    public void Diff_DetectsScalarChange()
    {
        var before = Parse("""{"name": "Harry", "city": "Bonn"}""");
        var after = Parse("""{"name": "Harald", "city": "Bonn"}""");

        var diff = JsonDiffEngine.Diff(before, after);

        var entry = Assert.Single(diff.Entries);
        Assert.Equal("name", entry.Key);
        Assert.Equal("Harry", (string?)entry.Value.Old);
        Assert.Equal("Harald", (string?)entry.Value.New);
    }

    [Fact]
    public void Diff_DistinguishesAddedField_FromNullValue()
    {
        var addedDiff = JsonDiffEngine.Diff(
            Parse("""{}"""),
            Parse("""{"email": "x@y.z"}"""));

        var changedFromNullDiff = JsonDiffEngine.Diff(
            Parse("""{"email": null}"""),
            Parse("""{"email": "x@y.z"}"""));

        Assert.False(addedDiff.Entries["email"].HasOld);
        Assert.True(changedFromNullDiff.Entries["email"].HasOld);
        Assert.Null(changedFromNullDiff.Entries["email"].Old);
    }

    [Fact]
    public void Diff_RecursesIntoNestedObjects()
    {
        var before = Parse("""{"address": {"city": "Bonn", "zip": "53111"}}""");
        var after = Parse("""{"address": {"city": "Köln", "zip": "53111"}}""");

        var diff = JsonDiffEngine.Diff(before, after);

        var entry = Assert.Single(diff.Entries);
        Assert.Equal("address.city", entry.Key);
        Assert.Equal("Bonn", (string?)entry.Value.Old);
        Assert.Equal("Köln", (string?)entry.Value.New);
    }

    [Fact]
    public void Diff_TreatsArraysAsAtomicValues()
    {
        var before = Parse("""{"roles": ["user"]}""");
        var after = Parse("""{"roles": ["user", "admin"]}""");

        var diff = JsonDiffEngine.Diff(before, after);

        var entry = Assert.Single(diff.Entries);
        Assert.Equal("roles", entry.Key);
        Assert.True(JsonNode.DeepEquals(Node("""["user"]"""), entry.Value.Old));
        Assert.True(JsonNode.DeepEquals(Node("""["user", "admin"]"""), entry.Value.New));
    }

    [Fact]
    public void Diff_IsEmptyForEqualDocuments_RegardlessOfKeyOrder()
    {
        var before = Parse("""{"a": 1, "b": {"x": true}}""");
        var after = Parse("""{"b": {"x": true}, "a": 1}""");

        Assert.True(JsonDiffEngine.Diff(before, after).IsEmpty);
    }

    [Fact]
    public void Diff_ForInsert_HasOnlyNewSides()
    {
        var diff = JsonDiffEngine.Diff(null, Parse("""{"name": "Harry", "address": {"city": "Bonn"}}"""));

        Assert.All(diff.Entries.Values, e => Assert.False(e.HasOld));
        Assert.Contains("name", diff.Paths);
        Assert.Contains("address", diff.Paths);
    }

    [Fact]
    public void Diff_ForDelete_HasOnlyOldSides()
    {
        var diff = JsonDiffEngine.Diff(Parse("""{"name": "Harry"}"""), null);

        Assert.All(diff.Entries.Values, e => Assert.False(e.HasNew));
    }

    [Fact]
    public void Diff_TypeChangeAtPath_ProducesAtomicEntry_NotRecursion()
    {
        // Object → scalar must yield ONE entry carrying the whole old object;
        // recursing here would break reversibility.
        var before = Parse("""{"address": {"city": "Bonn", "zip": "53111"}}""");
        var after = Parse("""{"address": "Bonn, 53111"}""");

        var diff = JsonDiffEngine.Diff(before, after);

        var entry = Assert.Single(diff.Entries);
        Assert.Equal("address", entry.Key);
        Assert.True(JsonNode.DeepEquals(Node("""{"city": "Bonn", "zip": "53111"}"""), entry.Value.Old));
        Assert.Equal("Bonn, 53111", (string?)entry.Value.New);
    }

    [Theory]
    [InlineData(
        """{"name": "Harry", "email": null, "address": {"city": "Bonn", "zip": "53111"}, "roles": ["user"]}""",
        """{"name": "Harald", "address": {"city": "Köln"}, "roles": ["user", "admin"], "active": true}""")]
    [InlineData("""{}""", """{"a": {"b": {"c": 1}}}""")]
    [InlineData("""{"a": {"b": {"c": 1}}}""", """{}""")]
    [InlineData("""{"v": 1}""", """{"v": 1}""")]
    [InlineData("""{"n": null}""", """{"n": 0}""")]
    // Typwechsel auf Pfaden — Objekt ↔ Skalar / Objekt ↔ null / Skalar ↔ Objekt
    [InlineData("""{"a": {"b": 1}}""", """{"a": "flat"}""")]
    [InlineData("""{"a": {"b": 1}}""", """{"a": null}""")]
    [InlineData("""{"a": 42}""", """{"a": {"b": {"c": true}}}""")]
    [InlineData("""{"a": {"b": 1}}""", """{"a": [1, 2]}""")]
    // Tiefe Teiländerung: nur ein Blatt in tiefer Struktur ändert sich
    [InlineData(
        """{"l1": {"l2": {"l3": {"keep": "x", "change": 1}}}}""",
        """{"l1": {"l2": {"l3": {"keep": "x", "change": 2}}}}""")]
    // Objekt mit leeren Objekten und null-Werten gemischt
    [InlineData("""{"a": {}, "b": {"c": null}}""", """{"a": {"x": 1}, "b": null}""")]
    public void Diff_RoundTrips_ForwardAndBackward(string beforeJson, string afterJson)
    {
        var before = Parse(beforeJson);
        var after = Parse(afterJson);

        var diff = JsonDiffEngine.Diff(before, after);

        Assert.True(JsonNode.DeepEquals(after, JsonDiffEngine.Apply(before, diff)),
            "Apply(before, diff) must yield after");
        Assert.True(JsonNode.DeepEquals(before, JsonDiffEngine.ApplyReverse(after, diff)),
            "ApplyReverse(after, diff) must yield before");
    }

    [Fact]
    public void WireFormat_RoundTrips()
    {
        var diff = JsonDiffEngine.Diff(
            Parse("""{"name": "Harry", "email": null, "gone": 1}"""),
            Parse("""{"name": "Harald", "email": "x@y.z", "added": true}"""));

        var restored = DocumentDiff.FromJson(diff.ToJson());

        Assert.Equal(diff.Paths, restored.Paths);
        foreach (var (path, entry) in diff.Entries)
        {
            Assert.Equal(entry.HasOld, restored.Entries[path].HasOld);
            Assert.Equal(entry.HasNew, restored.Entries[path].HasNew);
            Assert.True(JsonNode.DeepEquals(entry.Old, restored.Entries[path].Old));
            Assert.True(JsonNode.DeepEquals(entry.New, restored.Entries[path].New));
        }
    }

    [Fact]
    public void Apply_ThrowsLoudly_OnPolicyEntriesWithoutValues()
    {
        // Redacted/Reference/Hashed entries carry no values — reconstruction must fail,
        // never produce silently wrong state (ADR-007/008).
        var diff = DocumentDiff.FromJson((JsonObject)JsonNode.Parse(
            """{"email": {"changed": true}}""")!);

        Assert.Throws<InvalidOperationException>(() => JsonDiffEngine.Apply(new JsonObject(), diff));
        Assert.Throws<InvalidOperationException>(() => JsonDiffEngine.ApplyReverse(new JsonObject(), diff));
    }

    [Fact]
    public void Diff_EscapesADotInAKey_SoADictionaryKeyIsOneSegment()
    {
        var before = Parse("""{"settings": {}}""");
        var after = Parse("""{"settings": {"feature.enabled": true}}""");

        var diff = JsonDiffEngine.Diff(before, after);

        var entry = Assert.Single(diff.Entries);
        Assert.Equal("settings.feature\\.enabled", entry.Key);
        Assert.True(JsonNode.DeepEquals(after, JsonDiffEngine.Apply(before, diff)));
        Assert.True(JsonNode.DeepEquals(before, JsonDiffEngine.ApplyReverse(after, diff)));
    }

    [Theory]
    [InlineData("""{"a": {}}""", """{"a": {"b.c": {"d": 1}}}""")]
    [InlineData("""{"a": {"b\\c": 1}}""", """{"a": {"b\\c": 2}}""")]
    [InlineData("""{"a": {"x\\": {"y": 1}}}""", """{"a": {"x\\": {"y": 2}}}""")]
    [InlineData("""{"a.b": 1, "a": {"b": 2}}""", """{"a.b": 3, "a": {"b": 4}}""")]
    [InlineData("""{}""", """{"k.1": {"k.2": {"k.3": true}}}""")]
    public void Diff_RoundTrips_ForKeysWithDotsAndBackslashes(string beforeJson, string afterJson)
    {
        var before = Parse(beforeJson);
        var after = Parse(afterJson);

        var diff = JsonDiffEngine.Diff(before, after);

        Assert.True(JsonNode.DeepEquals(after, JsonDiffEngine.Apply(before, diff)));
        Assert.True(JsonNode.DeepEquals(before, JsonDiffEngine.ApplyReverse(after, diff)));
        Assert.True(JsonNode.DeepEquals(diff.ToJson(), DocumentDiff.FromJson(diff.ToJson()).ToJson()));
    }

    [Fact]
    public void Diff_RejectsAnEmptyKey_BecauseAPathCannotNameIt()
    {
        var before = Parse("""{"a": {}}""");
        var after = Parse("""{"a": {"": 1}}""");

        Assert.Throws<NotSupportedException>(() => JsonDiffEngine.Diff(before, after));
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonNode? Node(string json) => JsonNode.Parse(json);
}
