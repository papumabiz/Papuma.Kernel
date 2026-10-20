// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Evaluation prototype: the diff of <see cref="JsonDiffEngine"/> computed on UTF-8 JSON text through
/// <see cref="JsonDocument"/>, without building a node tree. Not wired into the sessions; it exists so the
/// differential tests and the pipeline benchmarks can hold it against the current engine.
/// </summary>
/// <remarks>
/// <para>Contract (deliberately stricter than the current engine where that one is inconsistent):</para>
/// <list type="bullet">
/// <item>An absent document (<c>null</c>) is the empty object (spec changes.md §1): every top-level field of the
/// other side becomes one added or removed entry carrying its whole value.</item>
/// <item>Both documents must be JSON objects (<see cref="ArgumentException"/>), parse with at most
/// <see cref="JsonLimits.MaxDepth"/> levels and without duplicate property names (<see cref="JsonException"/>).</item>
/// <item>An empty property name anywhere in either document is rejected (<see cref="NotSupportedException"/>),
/// whether or not its subtree changed — the outcome never depends on which parts happen to be equal.</item>
/// <item>The result owns copies of the changed values and never refers to the input buffers, which may be
/// reused or released as soon as the call returns.</item>
/// </list>
/// <para>Unchanged subtrees are skipped by comparing their raw bytes: identical bytes are always equal JSON.
/// Differing bytes fall back to a semantic comparison, so canonical input (PostgreSQL <c>jsonb</c> text) only
/// makes the diff faster, it is never required for a correct result.</para>
/// </remarks>
internal static class Utf8JsonDiffEngine
{
    /// <summary>The parser options every input goes through.</summary>
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = JsonLimits.MaxDepth,
        AllowDuplicateProperties = false,
    };

    private static readonly JsonElement EmptyObject = CreateEmptyObject();

    // No ReadOnlyMemory<byte>? overload on purpose: a null byte[] converts implicitly to an *empty* memory,
    // not to null, so "absent" would silently become "empty input". Absence has its own methods instead.

    /// <summary>Diffs two documents given as UTF-8 JSON.</summary>
    public static Utf8Diff Diff(ReadOnlyMemory<byte> before, ReadOnlyMemory<byte> after)
    {
        using var beforeDocument = Parse(before, nameof(before));
        using var afterDocument = Parse(after, nameof(after));
        return Diff(beforeDocument, afterDocument);
    }

    /// <summary>Diffs an insert: the absent before-state against <paramref name="after"/> (UTF-8 JSON).</summary>
    public static Utf8Diff Insert(ReadOnlyMemory<byte> after)
    {
        using var afterDocument = Parse(after, nameof(after));
        return Diff(null, afterDocument);
    }

    /// <summary>Diffs a delete: <paramref name="before"/> (UTF-8 JSON) against the absent after-state.</summary>
    public static Utf8Diff Delete(ReadOnlyMemory<byte> before)
    {
        using var beforeDocument = Parse(before, nameof(before));
        return Diff(beforeDocument, null);
    }

    /// <summary>Diffs two documents given as JSON text; <c>null</c> stands for an absent document.</summary>
    public static Utf8Diff Diff(string? before, string? after)
    {
        using var beforeDocument = before is null ? null : Parse(before, nameof(before));
        using var afterDocument = after is null ? null : Parse(after, nameof(after));
        return Diff(beforeDocument, afterDocument);
    }

    /// <summary>Parses and validates one UTF-8 input. The document refers to <paramref name="utf8Json"/>.</summary>
    internal static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json, string paramName) =>
        Validated(JsonDocument.Parse(utf8Json, DocumentOptions), paramName);

    /// <summary>Parses and validates one text input. The document owns its UTF-8 copy.</summary>
    internal static JsonDocument Parse(string json, string paramName)
    {
        ArgumentNullException.ThrowIfNull(json, paramName);
        return Validated(JsonDocument.Parse(json, DocumentOptions), paramName);
    }

    /// <summary>
    /// Diffs two parsed documents (<see cref="Parse(ReadOnlyMemory{byte}, string)"/>). The result is independent
    /// of both documents, which the caller may dispose right afterwards.
    /// </summary>
    internal static Utf8Diff Diff(JsonDocument? before, JsonDocument? after)
    {
        var pending = new List<PendingEntry>();
        DiffObject(
            before?.RootElement ?? EmptyObject,
            after?.RootElement ?? EmptyObject,
            prefix: [],
            pending);
        return Utf8Diff.Create(pending);
    }

    private static JsonDocument Validated(JsonDocument document, string paramName)
    {
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"A document must be a JSON object, not {document.RootElement.ValueKind}.", paramName);
            }

            RejectEmptyPropertyNames(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    // One forward scan over the raw text: a property name whose raw (escaped) form is empty is the empty
    // key — an escape sequence always spells at least one character.
    private static void RejectEmptyPropertyNames(JsonElement root)
    {
        var reader = new Utf8JsonReader(
            JsonMarshal.GetRawUtf8Value(root),
            new JsonReaderOptions { MaxDepth = JsonLimits.MaxDepth });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueSpan.IsEmpty)
            {
                throw new NotSupportedException(
                    "An object key is empty and cannot be represented as a diff path. Empty keys are not supported in diffed documents.");
            }
        }
    }

    private static void DiffObject(JsonElement before, JsonElement after, List<string> prefix, List<PendingEntry> pending)
    {
        if (RawEquals(before, after))
        {
            return; // byte-identical subtree
        }

        var afterCount = after.GetPropertyCount();
        var afterProperties = ArrayPool<JsonProperty>.Shared.Rent(afterCount);
        var matched = ArrayPool<bool>.Shared.Rent(afterCount);
        try
        {
            var filled = 0;
            foreach (var property in after.EnumerateObject())
            {
                afterProperties[filled++] = property;
            }

            Array.Clear(matched, 0, afterCount);
            Dictionary<string, int>? byName = null;
            var position = 0;
            foreach (var beforeProperty in before.EnumerateObject())
            {
                var index = Match(beforeProperty, position++, afterProperties, afterCount, matched, ref byName);
                if (index < 0)
                {
                    pending.Add(new PendingEntry(PathOf(prefix, beforeProperty.Name), beforeProperty.Value, null));
                    continue;
                }

                matched[index] = true;
                var oldValue = beforeProperty.Value;
                var newValue = afterProperties[index].Value;
                if (oldValue.ValueKind == JsonValueKind.Object && newValue.ValueKind == JsonValueKind.Object)
                {
                    prefix.Add(beforeProperty.Name);
                    DiffObject(oldValue, newValue, prefix, pending);
                    prefix.RemoveAt(prefix.Count - 1);
                }
                else if (!RawEquals(oldValue, newValue) && !JsonElement.DeepEquals(oldValue, newValue))
                {
                    pending.Add(new PendingEntry(PathOf(prefix, beforeProperty.Name), oldValue, newValue));
                }
            }

            for (var i = 0; i < afterCount; i++)
            {
                if (!matched[i])
                {
                    pending.Add(new PendingEntry(PathOf(prefix, afterProperties[i].Name), null, afterProperties[i].Value));
                }
            }
        }
        finally
        {
            // Cleared: a pooled array must not keep the documents (and their buffers) reachable.
            ArrayPool<JsonProperty>.Shared.Return(afterProperties, clearArray: true);
            ArrayPool<bool>.Shared.Return(matched);
        }
    }

    // Both sides usually list their keys in the same order (jsonb sorts them), so the property at the same
    // position is tried first by its raw bytes; anything else goes through a by-name index of the decoded names.
    private static int Match(
        JsonProperty beforeProperty,
        int position,
        JsonProperty[] afterProperties,
        int afterCount,
        bool[] matched,
        ref Dictionary<string, int>? byName)
    {
        if (position < afterCount
            && !matched[position]
            && JsonMarshal.GetRawUtf8PropertyName(beforeProperty)
                .SequenceEqual(JsonMarshal.GetRawUtf8PropertyName(afterProperties[position])))
        {
            return position;
        }

        if (byName is null)
        {
            byName = new Dictionary<string, int>(afterCount, StringComparer.Ordinal);
            for (var i = 0; i < afterCount; i++)
            {
                byName[afterProperties[i].Name] = i; // names are unique: duplicates are rejected at parse time
            }
        }

        return byName.TryGetValue(beforeProperty.Name, out var index) ? index : -1;
    }

    private static bool RawEquals(JsonElement x, JsonElement y) =>
        JsonMarshal.GetRawUtf8Value(x).SequenceEqual(JsonMarshal.GetRawUtf8Value(y));

    private static string PathOf(List<string> prefix, string name) =>
        prefix.Count == 0 ? DiffPath.EscapeSegment(name) : DiffPath.Join([.. prefix, name]);

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>A diff entry whose values still point into the parsed documents.</summary>
    internal readonly record struct PendingEntry(string Path, JsonElement? Old, JsonElement? New);
}
