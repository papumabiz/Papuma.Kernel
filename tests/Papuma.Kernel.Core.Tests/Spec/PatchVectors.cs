// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.Core.Tests.Spec;

/// <summary>Loads <c>docs/spec/vectors/patch.json</c> and checks an engine's result against it.</summary>
internal static class PatchVectors
{
    private static readonly string File = System.IO.Path.Combine(FindRepoRoot(), "docs", "spec", "vectors", "patch.json");

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Cases())
        {
            data.Add((string)vector!["name"]!);
        }

        return data;
    }

    public static JsonObject Load(string name) =>
        Cases().Cast<JsonObject>().Single(c => (string?)c["name"] == name);

    public static List<PatchOperation> Operations(JsonObject vector) =>
        [.. ((JsonArray)vector["ops"]!).Cast<JsonObject>().Select(op =>
        {
            var path = ((JsonArray)op["path"]!).Select(s => (string)s!).ToArray();
            return (string)op["op"]! switch
            {
                "set" => new PatchOperation(PatchOperationKind.Set, path, op["value"]?.DeepClone(), IncrementBy: 0),
                "remove" => new PatchOperation(PatchOperationKind.Remove, path, Value: null, IncrementBy: 0),
                "increment" => new PatchOperation(PatchOperationKind.Increment, path, Value: null, (long)op["by"]!),
                var other => throw new InvalidOperationException($"Unknown patch op '{other}'."),
            };
        })];

    /// <summary>
    /// Asserts the engine's outcome against the vector's target semantics, or against the
    /// pinned <c>knownDivergence</c> for this engine when one is declared.
    /// </summary>
    public static void Assert(JsonObject vector, string engine, JsonNode? after, string? error)
    {
        var divergence = (vector["knownDivergence"] as JsonObject)?[engine] as JsonObject;
        var expectedAfter = (divergence ?? vector)["after"];
        var expectedError = (string?)(divergence ?? vector)["error"];

        var actual = error is null ? after?.ToJsonString() : $"error:{error}";
        var expected = expectedError is null ? expectedAfter?.ToJsonString() : $"error:{expectedError}";
        var matches = error is null && expectedError is null
            ? JsonNode.DeepEquals(expectedAfter, after)
            : error == expectedError;

        Xunit.Assert.True(
            matches,
            $"{engine}: expected {expected}{(divergence is null ? string.Empty : " (pinned divergence)")}, " +
            $"actual {actual}");
    }

    private static JsonArray Cases() =>
        (JsonArray)((JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(File))!)["cases"]!;

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "Papuma.Kernel.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Papuma.Kernel.slnx) not found above the test output.");
    }
}
