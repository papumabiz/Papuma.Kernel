// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// A reversible field-level diff between two document states (ADR-004),
/// keyed by dot-separated JSON paths (e.g. <c>address.city</c>).
/// </summary>
/// <remarks>
/// Arrays are treated as atomic leaf values: any difference produces one entry for the
/// array path with the full old and new array. Object keys containing '.' are not
/// supported as diff paths.
/// </remarks>
public sealed class DocumentDiff
{
    /// <summary>An empty diff (no field changed).</summary>
    public static readonly DocumentDiff Empty = new(new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal));

    private readonly SortedDictionary<string, DiffEntry> _entries;

    internal DocumentDiff(SortedDictionary<string, DiffEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>Gets the diff entries keyed by JSON path, ordered ordinally.</summary>
    public IReadOnlyDictionary<string, DiffEntry> Entries => _entries;

    /// <summary>Gets the changed JSON paths.</summary>
    public IReadOnlyCollection<string> Paths => _entries.Keys;

    /// <summary>Gets a value indicating whether no field changed.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// Serializes the diff to its JSONB wire format:
    /// <c>{ "path": { "old": ..., "new": ... } }</c> with <c>old</c>/<c>new</c> keys
    /// omitted when the field did not exist on that side.
    /// </summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var (path, entry) in _entries)
        {
            var entryJson = new JsonObject();
            if (entry.HasOld)
            {
                entryJson["old"] = entry.Old?.DeepClone();
            }

            if (entry.HasNew)
            {
                entryJson["new"] = entry.New?.DeepClone();
            }

            json[path] = entryJson;
        }

        return json;
    }

    /// <summary>
    /// Deserializes a diff from its JSONB wire format (see <see cref="ToJson"/>).
    /// </summary>
    /// <param name="json">The wire-format JSON object.</param>
    public static DocumentDiff FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var entries = new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal);
        foreach (var (path, node) in json)
        {
            if (node is not JsonObject entryJson)
            {
                throw new ArgumentException($"Diff entry at '{path}' is not an object.", nameof(json));
            }

            var hasOld = entryJson.ContainsKey("old");
            var hasNew = entryJson.ContainsKey("new");
            var old = hasOld ? entryJson["old"]?.DeepClone() : null;
            var @new = hasNew ? entryJson["new"]?.DeepClone() : null;

            entries[path] = (hasOld, hasNew) switch
            {
                (true, true) => DiffEntry.Changed(old, @new),
                (false, true) => DiffEntry.Added(@new),
                (true, false) => DiffEntry.Removed(old),
                _ => throw new ArgumentException($"Diff entry at '{path}' has neither 'old' nor 'new'.", nameof(json)),
            };
        }

        return new DocumentDiff(entries);
    }
}
