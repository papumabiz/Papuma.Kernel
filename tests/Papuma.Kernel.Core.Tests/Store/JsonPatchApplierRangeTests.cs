// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.Core.Tests.Store;

/// <summary>
/// <c>Increment</c> in the in-process engine never overflows unhandled and never rounds silently (review C-4);
/// the cases are also pinned as vectors in <c>docs/spec/vectors/patch.json</c>.
/// </summary>
public sealed class JsonPatchApplierRangeTests
{
    private const string DecimalMax = "79228162514264337593543950335";

    private static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static List<PatchOperation> Increment(long by, params string[] path) =>
        [new PatchOperation(PatchOperationKind.Increment, path, Value: null, by)];

    [Theory]
    [InlineData("""{"n":1e30}""", 1)]
    [InlineData("""{"n":-1e30}""", -1)]
    [InlineData("""{"n":1E400}""", 1)]
    [InlineData("""{"n":79228162514264337593543950336}""", 1)] // decimal.MaxValue + 1
    [InlineData("""{"n":0.1234567890123456789012345678901}""", 1)] // more digits than a decimal holds
    [InlineData("""{"n":1e-30}""", 1)] // would round to 0
    [InlineData("""{"n":79228162514264337593543950335}""", 1)] // the result overflows
    [InlineData("""{"n":-79228162514264337593543950335}""", -1)]
    [InlineData("""{"n":0.1234567890123456789012345678}""", long.MaxValue)] // the sum would round to 28 digits
    public void ANumberOrResultOutsideTheDecimalRange_FailsTyped_WithTheLocation(string json, long by)
    {
        var document = Doc(json);

        var ex = Assert.Throws<PatchValueOutOfRangeException>(
            () => JsonPatchApplier.Apply(document, Increment(by, "n"), "Doc", "42"));

        Assert.Equal("Doc", ex.DocumentType);
        Assert.Equal("42", ex.DocumentId);
        Assert.Equal("n", ex.FieldPath);
        Assert.Equal(json, document.ToJsonString());
    }

    [Theory]
    [InlineData("""{"n":1}""", 1, "2")]
    [InlineData("""{"n":-5}""", 3, "-2")]
    [InlineData("""{"n":79228162514264337593543950334}""", 1, DecimalMax)]
    [InlineData("""{"n":-79228162514264337593543950334}""", -1, "-" + DecimalMax)]
    [InlineData("""{"n":0.1234567890123456789012345678}""", 0, "0.1234567890123456789012345678")]
    [InlineData("""{"n":1.50}""", 1, "2.50")]
    [InlineData("""{"n":null}""", long.MaxValue, "9223372036854775807")]
    [InlineData("""{}""", long.MinValue, "-9223372036854775808")]
    public void ANumberWithinTheRange_IsIncrementedExactly(string json, long by, string expected)
    {
        var document = Doc(json);

        JsonPatchApplier.Apply(document, Increment(by, "n"), "Doc", "1");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), document["n"]), document.ToJsonString());
    }

    [Fact]
    public void ANonNumericValue_StillFailsAsNonNumeric_NotAsOutOfRange() =>
        Assert.Throws<NonNumericIncrementException>(
            () => JsonPatchApplier.Apply(Doc("""{"n":"1e30"}"""), Increment(1, "n"), "Doc", "1"));

    [Theory]
    [InlineData("""{"n":1e30}""", 1)]
    [InlineData("""{"n":79228162514264337593543950335}""", 1)]
    public void WhenTheDatabaseAlreadyDidTheArithmetic_OutOfRangeIsNotRechecked(string json, long by)
    {
        // PostgreSQL's numeric has no such limit; EnsureTargetsExist only checks that the targets are reachable.
        var before = Doc(json);

        JsonPatchApplier.EnsureTargetsExist(before, Increment(by, "n"), "Doc", "1");

        Assert.Equal(json, before.ToJsonString());
    }

    [Fact]
    public void TheExceptionRejectsABlankDocumentType() =>
        Assert.Throws<ArgumentException>(() => new PatchValueOutOfRangeException(" ", "1", "n"));
}
