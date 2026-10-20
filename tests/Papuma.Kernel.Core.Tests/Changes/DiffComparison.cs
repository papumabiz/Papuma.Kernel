// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Core.Tests.Robustness;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>How a generated document is written out as text before both engines see it.</summary>
internal enum JsonSpelling
{
    /// <summary><c>ToJsonString()</c> with the default encoder: compact, non-ASCII escaped as <c>\uXXXX</c>.</summary>
    Compact,

    /// <summary>Compact, non-ASCII written literally.</summary>
    Relaxed,

    /// <summary>Indented, non-ASCII written literally.</summary>
    Pretty,

    /// <summary>Same JSON value, keys shuffled and numbers respelled (<see cref="JsonGenerator.Equivalent"/>).</summary>
    Equivalent,
}

/// <summary>
/// Shared by the differential tests of both test projects: full-semantics comparison of two diffs, the
/// reconstruction check, the text spellings, and the case count (<c>PAPUMA_DIFF_CASES</c> scales it up locally).
/// </summary>
internal static class DiffComparison
{
    private static readonly JsonSerializerOptions RelaxedOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>
    /// Returns the CI case count, or the value of <c>PAPUMA_DIFF_CASES</c> when it is set (e.g. <c>100000</c>
    /// for a long local run).
    /// </summary>
    public static int Cases(int ciDefault) =>
        int.TryParse(Environment.GetEnvironmentVariable("PAPUMA_DIFF_CASES"), NumberStyles.None, CultureInfo.InvariantCulture, out var cases)
        && cases > 0
            ? cases
            : ciDefault;

    /// <summary>Writes <paramref name="document"/> in the given spelling.</summary>
    public static string Spell(JsonObject document, JsonSpelling spelling, JsonGenerator generator) => spelling switch
    {
        JsonSpelling.Compact => document.ToJsonString(),
        JsonSpelling.Relaxed => document.ToJsonString(RelaxedOptions),
        JsonSpelling.Pretty => document.ToJsonString(PrettyOptions),
        _ => generator.Equivalent(document)!.ToJsonString(RelaxedOptions),
    };

    /// <summary>
    /// Returns <c>null</c> when both diffs carry the same entries — same paths in the same order, same
    /// presence of each side, equal values — otherwise a description of the first difference.
    /// </summary>
    public static string? Difference(DocumentDiff expected, DocumentDiff actual)
    {
        if (!expected.Paths.SequenceEqual(actual.Paths, StringComparer.Ordinal))
        {
            return $"paths differ: expected [{string.Join(", ", expected.Paths)}], actual [{string.Join(", ", actual.Paths)}]";
        }

        foreach (var (path, want) in expected.Entries)
        {
            var got = actual.Entries[path];
            if (want.Kind != got.Kind || want.HasOld != got.HasOld || want.HasNew != got.HasNew)
            {
                return $"'{path}': kind/presence differ (expected {want.Kind} old={want.HasOld} new={want.HasNew}, " +
                       $"actual {got.Kind} old={got.HasOld} new={got.HasNew})";
            }

            if (!JsonNode.DeepEquals(want.Old, got.Old) || !JsonNode.DeepEquals(want.New, got.New))
            {
                return $"'{path}': values differ (expected {Show(want.Old)} -> {Show(want.New)}, actual {Show(got.Old)} -> {Show(got.New)})";
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>null</c> when <c>Apply(before, diff) == after</c> and <c>ApplyReverse(after, diff) == before</c>,
    /// an absent document being the empty object; otherwise what failed.
    /// </summary>
    public static string? Reconstruction(JsonObject? before, JsonObject? after, DocumentDiff diff)
    {
        var beforeState = before ?? new JsonObject();
        var afterState = after ?? new JsonObject();
        if (!JsonNode.DeepEquals(afterState, JsonDiffEngine.Apply(before, diff)))
        {
            return "Apply(before, diff) != after";
        }

        return JsonNode.DeepEquals(beforeState, JsonDiffEngine.ApplyReverse(after, diff))
            ? null
            : "ApplyReverse(after, diff) != before";
    }

    /// <summary>
    /// Diffs UTF-8 inputs where <c>null</c> marks an absent side, dispatching to <c>Insert</c>/<c>Delete</c>.
    /// </summary>
    public static Utf8Diff DiffUtf8(ReadOnlyMemory<byte>? before, ReadOnlyMemory<byte>? after) => (before, after) switch
    {
        ({ } b, { } a) => Utf8JsonDiffEngine.Diff(b, a),
        (null, { } a) => Utf8JsonDiffEngine.Insert(a),
        ({ } b, null) => Utf8JsonDiffEngine.Delete(b),
        _ => Utf8JsonDiffEngine.Diff((string?)null, null),
    };

    /// <summary>The UTF-8 bytes of <paramref name="text"/>, or <c>null</c> (not an empty memory) for <c>null</c>.</summary>
    public static ReadOnlyMemory<byte>? Utf8(string? text) =>
        text is null ? (ReadOnlyMemory<byte>?)null : System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary>Reads a wire-form diff (UTF-8 JSON) back into a <see cref="DocumentDiff"/>.</summary>
    public static DocumentDiff FromWire(ReadOnlySpan<byte> utf8) =>
        DocumentDiff.FromJson((JsonObject)JsonNode.Parse(utf8)!);

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";
}
