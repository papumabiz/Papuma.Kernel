// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// The erasure of history entries (<see cref="RedactionEngine.RedactDiffEntries"/>): an erasure path also reaches
/// an ancestor entry that carries the erased field inside its value — the shape every insert, delete and set or
/// cleared object had before policies were split per field, and still has for fields without a policy.
/// </summary>
public sealed class RedactionEngineTests
{
    private static JsonObject Diff(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void AnErasedNestedPath_RedactsTheWholeObjectEntryThatCarriesIt()
    {
        // Written before the split fix: the insert stored the street under "address".
        var diff = Diff("""{"address":{"new":{"street":"Main St 1","city":"Bonn"}},"name":{"new":"Harry"}}""");

        Assert.True(RedactionEngine.RedactDiffEntries(diff, ["address.street"]));

        Assert.Equal("""{"address":{"changed":true},"name":{"new":"Harry"}}""", diff.ToJsonString());
    }

    [Theory]
    [InlineData("""{"address":{"old":{"street":"Main St 1","city":"Bonn"}}}""")] // delete / cleared
    [InlineData("""{"address":{"old":{"street":"Main St 1"},"new":"moved"}}""")] // type change, old side
    [InlineData("""{"address":{"old":null,"new":{"street":"Main St 1"}}}""")] // set from null
    public void EitherSide_CanCarryTheErasedField(string json)
    {
        var diff = Diff(json);

        Assert.True(RedactionEngine.RedactDiffEntries(diff, ["address.street"]));

        Assert.DoesNotContain("Main St", diff.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeeperErasedPath_IsFoundInsideTheValue()
    {
        var diff = Diff("""{"a":{"new":{"b":{"c":"secret","d":1}}}}""");

        Assert.True(RedactionEngine.RedactDiffEntries(diff, ["a.b.c"]));

        Assert.Equal("""{"a":{"changed":true}}""", diff.ToJsonString());
    }

    [Fact]
    public void AnAncestorWhoseValueDoesNotHoldTheField_IsLeftAlone()
    {
        // The split form written since the fix: the container no longer carries the protected field.
        var diff = Diff("""{"address":{"new":{"city":"Bonn"}},"address.street":{"changed":true}}""");

        Assert.False(RedactionEngine.RedactDiffEntries(diff, ["address.street"]));

        Assert.Equal("Bonn", (string?)diff["address"]!["new"]!["city"]);
    }

    [Theory]
    [InlineData("""{"address":{"new":["Main St 1"]}}""")] // an array is atomic: no field path inside it
    [InlineData("""{"address":{"new":"Main St 1"}}""")]
    [InlineData("""{"addressBook":{"new":{"street":"Main St 1"}}}""")] // a sibling with a longer name
    public void AnEntryThatCannotHoldThePath_IsLeftAlone(string json)
    {
        var diff = Diff(json);

        Assert.False(RedactionEngine.RedactDiffEntries(diff, ["address.street"]));
    }

    [Fact]
    public void EscapedKeys_AreMatchedSegmentBySegment()
    {
        var diff = Diff("""{"settings":{"new":{"api.key":"s3cret","api":{"key":"other"}}}}""");

        Assert.True(RedactionEngine.RedactDiffEntries(diff, ["settings.api\\.key"]));
        Assert.Equal("""{"settings":{"changed":true}}""", diff.ToJsonString());

        var nested = Diff("""{"settings":{"new":{"api.key":"s3cret"}}}""");
        Assert.False(RedactionEngine.RedactDiffEntries(nested, ["settings.api.key"])); // a different field
    }

    [Fact]
    public void ExactAndDescendantPaths_AreStillCovered()
    {
        var diff = Diff("""{"address.city":{"old":"Bonn","new":"Köln"},"address.street":{"old":"a","new":"b"},"name":{"new":"x"}}""");

        Assert.True(RedactionEngine.RedactDiffEntries(diff, ["address"]));

        Assert.Equal("""{"address.city":{"changed":true},"address.street":{"changed":true},"name":{"new":"x"}}""", diff.ToJsonString());
    }
}
