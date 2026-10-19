// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Spec;

/// <summary>
/// Runs the language-neutral conformance vectors in <c>docs/spec/vectors</c> against the
/// kernel's storage-neutral Core. A second implementation (another language, another
/// engine) proves compatibility by passing the same files.
/// </summary>
public sealed class SpecVectorTests
{
    private static readonly string VectorsDirectory = Path.Combine(FindRepoRoot(), "docs", "spec", "vectors");

    public static TheoryData<string> DiffCases() => CaseNames("diff.json");

    public static TheoryData<string> PolicyCases() => CaseNames("policy.json");

    [Theory]
    [MemberData(nameof(DiffCases))]
    public void Diff_MatchesVector(string name)
    {
        var vector = LoadCase("diff.json", name);
        var before = vector["before"] as JsonObject;
        var after = vector["after"] as JsonObject;

        if (vector["error"] is JsonNode error)
        {
            Assert.Equal("unsupported-key", (string?)error);
            Assert.Throws<NotSupportedException>(() => JsonDiffEngine.Diff(before, after));
            return;
        }

        var expected = (JsonObject)vector["diff"]!;
        var diff = JsonDiffEngine.Diff(before, after);
        var wire = diff.ToJson();

        AssertJsonEqual(expected, wire);

        if (vector["orderedPaths"] is JsonArray ordered)
        {
            Assert.Equal(ordered.Select(p => (string?)p), wire.Select(p => p.Key));
        }

        // The wire format must survive a parse/serialize round trip unchanged.
        AssertJsonEqual(expected, DocumentDiff.FromJson(expected).ToJson());

        // Reversibility (ADR-004). An absent document is the empty object on either side.
        AssertJsonEqual(after ?? new JsonObject(), JsonDiffEngine.Apply(before, diff));
        AssertJsonEqual(before ?? new JsonObject(), JsonDiffEngine.ApplyReverse(after, diff));
    }

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public void Policy_MatchesVector(string name)
    {
        var vector = LoadCase("policy.json", name);
        var policies = ((JsonObject)vector["policies"]!)
            .ToDictionary(p => p.Key, p => Enum.Parse<FieldPolicy>((string)p.Value!));
        var documentType = (string)vector["documentType"]!;
        var documentId = (string)vector["documentId"]!;

        var metadata = new DocumentTypeMetadata(
            documentType,
            typeof(object),
            policies,
            keys: [],
            idGetter: _ => documentId,
            upcasters: [],
            validator: null,
            exposedToMcp: false);

        // A case with a "hashKey" runs under the keyed v2 hash (ADR-025); without one, the legacy v1 hash.
        IHashKeyProvider? hashKeys = null;
        if (vector["hashKey"] is JsonObject hashKey)
        {
            hashKeys = new MasterKeyHashKeyProvider(
                Convert.FromHexString((string)hashKey["masterKeyHex"]!), (string)hashKey["kid"]!);
        }

        var tenantId = (string?)vector["tenantId"];
        var scope = string.IsNullOrEmpty(tenantId) ? ScopeContext.Platform() : ScopeContext.Tenant(tenantId);

        var diff = DocumentDiff.FromJson((JsonObject)vector["diff"]!);
        var applied = PolicyApplier.Apply(diff, metadata, documentId, hashKeys, scope);

        AssertJsonEqual((JsonObject)vector["expected"]!, applied.ToJson());
    }

    [Fact]
    public void Vectors_HaveUniqueNames_AndAreNotEmpty()
    {
        foreach (var file in new[] { "diff.json", "policy.json" })
        {
            var names = Cases(file).Select(c => (string)c!["name"]!).ToList();
            Assert.NotEmpty(names);
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    private static TheoryData<string> CaseNames(string file)
    {
        var data = new TheoryData<string>();
        foreach (var vector in Cases(file))
        {
            data.Add((string)vector!["name"]!);
        }

        return data;
    }

    private static JsonObject LoadCase(string file, string name) =>
        Cases(file).Cast<JsonObject>().Single(c => (string?)c["name"] == name);

    private static JsonArray Cases(string file) =>
        (JsonArray)((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(VectorsDirectory, file)))!)["cases"]!;

    private static void AssertJsonEqual(JsonNode? expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Expected: {expected?.ToJsonString()}{Environment.NewLine}Actual:   {actual?.ToJsonString()}");

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Papuma.Kernel.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Papuma.Kernel.slnx) not found above the test output.");
    }
}
