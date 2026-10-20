// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// <see cref="DocumentDiff.FromJson"/> rejects malformed wire data with one typed exception, whatever is wrong
/// (review C-7) — and still reads every shape <see cref="DocumentDiff.ToJson"/> writes.
/// </summary>
public sealed class DocumentDiffFromJsonTests
{
    private static JsonObject Wire(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory]
    [InlineData("""{"a":{"ref":null}}""")]
    [InlineData("""{"a":{"ref":5}}""")]
    [InlineData("""{"a":{"ref":{"x":1}}}""")]
    [InlineData("""{"a":{"ref":""}}""")]
    [InlineData("""{"a":{"changed":true,"hash":5}}""")]
    [InlineData("""{"a":{"changed":true,"hash":{"x":1}}}""")]
    [InlineData("""{"a":{"changed":true,"hash":true}}""")]
    [InlineData("""{"a":5}""")]
    [InlineData("""{"a":null}""")]
    [InlineData("""{"a":{}}""")]
    public void MalformedWireData_ThrowsArgumentException_BlamingJson(string wire)
    {
        var ex = Assert.Throws<ArgumentException>(() => DocumentDiff.FromJson(Wire(wire)));

        Assert.Equal("json", ex.ParamName);
        Assert.IsType<ArgumentException>(ex); // not an ArgumentNullException / InvalidOperationException subtype
    }

    [Fact]
    public void NullJson_StillThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => DocumentDiff.FromJson(null!));

    [Theory]
    [InlineData("""{"a":{"old":1,"new":2}}""")]
    [InlineData("""{"a":{"new":"x"}}""")]
    [InlineData("""{"a":{"old":null}}""")]
    [InlineData("""{"a":{"changed":true}}""")]
    [InlineData("""{"a":{"ref":"User/1/iban"}}""")]
    [InlineData("""{"a":{"changed":true,"hash":"v2:k1:abcd"}}""")]
    [InlineData("""{"a":{"changed":true,"hash":null}}""")] // a legacy/hand-written shape that has always been read
    public void WellFormedWireData_RoundTrips(string wire)
    {
        var diff = DocumentDiff.FromJson(Wire(wire));

        Assert.Single(diff.Entries);
        Assert.NotNull(diff.ToJson());
    }

    [Fact]
    public void AHashedEntryWithoutAHash_KeepsItsWireFormat()
    {
        var diff = new DocumentDiff(new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal)
        {
            ["a"] = DiffEntry.HashedEntry(null),
        });

        // The remove-side entry carries no hash (changes.md); the wire format must not gain "hash": null.
        Assert.Equal("""{"a":{"changed":true}}""", diff.ToJson().ToJsonString());
    }
}
