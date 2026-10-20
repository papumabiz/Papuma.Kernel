// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Applies field policies (ADR-007) to a raw diff before it reaches the change feed.
/// This runs inside the write transaction — no code path writes an unpoliced diff.
/// </summary>
internal static class PolicyApplier
{
    /// <summary>
    /// Transforms a raw tracked diff into its policy-applied form.
    /// </summary>
    /// <param name="diff">The raw diff produced by <see cref="JsonDiffEngine"/>.</param>
    /// <param name="metadata">The document type metadata carrying the policies.</param>
    /// <param name="documentId">The document id (for reference entries).</param>
    /// <param name="hashKeys">The hash key provider, or <c>null</c> for the legacy v1 hash (ADR-025).</param>
    /// <param name="scope">The scope of the write — the key of a keyed hash is derived per tenant.</param>
    public static DocumentDiff Apply(
        DocumentDiff diff,
        DocumentTypeMetadata metadata,
        string documentId,
        IHashKeyProvider? hashKeys,
        ScopeContext scope)
    {
        if (metadata.Policies.Count == 0 || diff.IsEmpty)
        {
            return diff;
        }

        var hash = new HashContext(hashKeys, scope, metadata.Name);
        var entries = new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal);
        foreach (var (path, entry) in SplitAtPolicies(diff, metadata).Entries)
        {
            switch (metadata.ResolvePolicy(path))
            {
                case FieldPolicy.Track:
                    entries[path] = entry;
                    break;

                case FieldPolicy.Redact:
                    entries[path] = DiffEntry.RedactedEntry();
                    break;

                case FieldPolicy.Reference:
                    entries[path] = DiffEntry.ReferenceEntry($"{metadata.Name}/{documentId}/{path}");
                    break;

                case FieldPolicy.Hash:
                    entries[path] = DiffEntry.HashedEntry(HashOf(entry, hash));
                    break;

                case FieldPolicy.DoNotTrack:
                    break; // field never reaches the feed
            }
        }

        return new DocumentDiff(entries);
    }

    /// <summary>
    /// Splits every tracked entry whose value carries a field with a different effective policy than the
    /// entry's own path, so that each policy applies to an entry of its own. Without this, an entry that
    /// carries a whole object — an insert, a delete, a field added or removed with an object value, a type
    /// change to or from an object — would store a nested protected field (<c>address.street</c>) under the
    /// unprotected path of its container (<c>address</c>).
    /// </summary>
    /// <remarks>
    /// The container entry keeps everything that shares its policy; each subtree with a different effective
    /// policy moves to its own entry below it (added or removed, from the side that held it). The result is
    /// still a tracked diff that <see cref="JsonDiffEngine.Apply"/> and <see cref="JsonDiffEngine.ApplyReverse"/>
    /// turn into the same states: a container sorts before its descendants, so it is set first, and removing a
    /// descendant whose container is gone is a no-op.
    /// </remarks>
    internal static DocumentDiff SplitAtPolicies(DocumentDiff diff, DocumentTypeMetadata metadata)
    {
        if (!diff.Entries.Any(e => NeedsSplit(e.Key, metadata)))
        {
            return diff;
        }

        var entries = new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal);
        foreach (var (path, entry) in diff.Entries)
        {
            Split(path, entry, metadata, entries);
        }

        return new DocumentDiff(entries);
    }

    private static void Split(string path, DiffEntry entry, DocumentTypeMetadata metadata, SortedDictionary<string, DiffEntry> into)
    {
        if (entry.Kind != DiffEntryKind.Tracked || !NeedsSplit(path, metadata))
        {
            into[path] = entry;
            return;
        }

        if (entry is { HasOld: true, Old: JsonObject oldObject, HasNew: true, New: JsonObject newObject })
        {
            // Not produced by the engine (it recurses into objects on both sides), but a valid entry: diff it.
            foreach (var (subPath, subEntry) in JsonDiffEngine.Diff(oldObject, newObject).Entries)
            {
                Split($"{path}{DiffPath.Separator}{subPath}", subEntry, metadata, into);
            }

            return;
        }

        // At most one side is an object now; only that side can hold protected fields.
        var policy = metadata.ResolvePolicy(path);
        var extracted = new List<(string Path, JsonNode? Value)>();
        var old = entry.HasOld ? Strip(path, entry.Old, policy, metadata, extracted) : null;
        var fromOld = extracted.Count;
        var @new = entry.HasNew ? Strip(path, entry.New, policy, metadata, extracted) : null;

        into[path] = (entry.HasOld, entry.HasNew) switch
        {
            (true, true) => DiffEntry.Changed(old, @new),
            (true, false) => DiffEntry.Removed(old),
            _ => DiffEntry.Added(@new),
        };

        for (var i = 0; i < extracted.Count; i++)
        {
            var (childPath, value) = extracted[i];
            Split(childPath, i < fromOld ? DiffEntry.Removed(value) : DiffEntry.Added(value), metadata, into);
        }
    }

    // Copies an object value without the subtrees whose effective policy differs from the container's,
    // collecting those subtrees (path and value) for entries of their own.
    private static JsonNode? Strip(
        string path,
        JsonNode? value,
        FieldPolicy containerPolicy,
        DocumentTypeMetadata metadata,
        List<(string Path, JsonNode? Value)> extracted)
    {
        if (value is not JsonObject obj)
        {
            return value?.DeepClone(); // no field paths inside a scalar or an (atomic) array
        }

        var copy = new JsonObject();
        foreach (var (key, child) in obj)
        {
            var childPath = DiffPath.Append(path, key);
            if (metadata.ResolvePolicy(childPath) != containerPolicy)
            {
                extracted.Add((childPath, child?.DeepClone()));
            }
            else if (child is JsonObject && NeedsSplit(childPath, metadata))
            {
                copy[key] = Strip(childPath, child, containerPolicy, metadata, extracted);
            }
            else
            {
                copy[key] = child?.DeepClone();
            }
        }

        return copy;
    }

    // Whether a policy declared strictly below the path differs from the path's own effective policy.
    // Paths are escaped (DiffPath), so "below" is a plain prefix test on "<path>.".
    private static bool NeedsSplit(string path, DocumentTypeMetadata metadata)
    {
        var policy = metadata.ResolvePolicy(path);
        var prefix = path + DiffPath.Separator;
        foreach (var (declared, declaredPolicy) in metadata.Policies)
        {
            if (declaredPolicy != policy && declared.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? HashOf(DiffEntry entry, HashContext hash) =>
        entry.HasNew ? hash.Hash(entry.New) : null; // field removed → nothing to hash
}
