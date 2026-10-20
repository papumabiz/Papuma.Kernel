// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// A reversible field-level diff between two document states (ADR-004),
/// keyed by dot-separated JSON paths (e.g. <c>address.city</c>); a <c>.</c> or <c>\</c> inside a key is escaped
/// as <c>\.</c> and <c>\\</c> (<c>settings.feature\.enabled</c>).
/// </summary>
/// <remarks>
/// Arrays are treated as atomic leaf values: any difference produces one entry for the
/// array path with the full old and new array. An empty object key is an empty path segment
/// (docs/spec/changes.md §2): <c>""</c> at the top, <c>a.</c> below <c>a</c>.
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
    /// Serializes the diff to its JSONB wire format (ADR-004/007):
    /// tracked entries as <c>{ "old": ..., "new": ... }</c> (keys omitted when the field
    /// did not exist on that side), policy entries as <c>{ "changed": true }</c>,
    /// <c>{ "ref": "..." }</c> or <c>{ "changed": true, "hash": "..." }</c>.
    /// </summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var (path, entry) in _entries)
        {
            var entryJson = new JsonObject();
            switch (entry.Kind)
            {
                case DiffEntryKind.Tracked:
                    if (entry.HasOld)
                    {
                        entryJson["old"] = entry.Old?.DeepClone();
                    }

                    if (entry.HasNew)
                    {
                        entryJson["new"] = entry.New?.DeepClone();
                    }

                    break;

                case DiffEntryKind.Redacted:
                    entryJson["changed"] = true;
                    break;

                case DiffEntryKind.Reference:
                    entryJson["ref"] = entry.Reference;
                    break;

                case DiffEntryKind.Hashed:
                    entryJson["changed"] = true;
                    if (entry.Hash is not null)
                    {
                        entryJson["hash"] = entry.Hash;
                    }

                    break;
            }

            json[path] = entryJson;
        }

        return json;
    }

    /// <summary>
    /// Deserializes a diff from its JSONB wire format (see <see cref="ToJson"/>).
    /// </summary>
    /// <param name="json">The wire-format JSON object.</param>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="json"/> is not a well-formed diff: an entry that is not an object, a <c>ref</c> that is not a
    /// non-empty string, a <c>hash</c> that is neither a string nor <c>null</c>, or an entry with neither
    /// <c>old</c> nor <c>new</c> nor a policy marker.
    /// </exception>
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

            if (entryJson.TryGetPropertyValue("ref", out var reference))
            {
                if (reference is not JsonValue refValue
                    || !refValue.TryGetValue<string>(out var referenceText)
                    || string.IsNullOrWhiteSpace(referenceText))
                {
                    throw new ArgumentException(
                        $"Diff entry at '{path}': 'ref' must be a non-empty string.", nameof(json));
                }

                entries[path] = DiffEntry.ReferenceEntry(referenceText);
                continue;
            }

            if (entryJson.TryGetPropertyValue("hash", out var hash))
            {
                // A missing or JSON null hash is the shape of a remove-side entry (changes.md); anything else must be text.
                string? hashText = null;
                if (hash is not null && (hash is not JsonValue hashValue || !hashValue.TryGetValue(out hashText)))
                {
                    throw new ArgumentException(
                        $"Diff entry at '{path}': 'hash' must be a string or null.", nameof(json));
                }

                entries[path] = DiffEntry.HashedEntry(hashText);
                continue;
            }

            if (entryJson.ContainsKey("changed"))
            {
                entries[path] = DiffEntry.RedactedEntry();
                continue;
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
