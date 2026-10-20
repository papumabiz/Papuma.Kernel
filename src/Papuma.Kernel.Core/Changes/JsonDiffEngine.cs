// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Computes and applies reversible field-level diffs between JSON documents (ADR-004).
/// </summary>
/// <remarks>
/// Invariants (covered by round-trip tests):
/// <c>Apply(before, Diff(before, after)) == after</c> and
/// <c>ApplyReverse(after, Diff(before, after)) == before</c>.
/// Nested objects are diffed recursively; arrays are atomic leaf values.
/// </remarks>
public static class JsonDiffEngine
{
    /// <summary>
    /// Computes the diff between two document states. Pass <c>null</c> for
    /// <paramref name="before"/> on insert and for <paramref name="after"/> on delete.
    /// </summary>
    public static DocumentDiff Diff(JsonObject? before, JsonObject? after)
    {
        EnsureDepth(before, after);
        var entries = new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal);
        DiffObject(before, after, prefix: null, entries);
        return new DocumentDiff(entries);
    }

    /// <summary>
    /// Applies a diff forward: transforms the before-state into the after-state.
    /// </summary>
    public static JsonObject Apply(JsonObject? before, DocumentDiff diff) =>
        ApplyCore(before, diff, useNew: true);

    /// <summary>
    /// Applies a diff backward: transforms the after-state back into the before-state.
    /// </summary>
    public static JsonObject ApplyReverse(JsonObject? after, DocumentDiff diff) =>
        ApplyCore(after, diff, useNew: false);

    private static void EnsureDepth(params JsonObject?[] documents)
    {
        foreach (var document in documents)
        {
            if (JsonLimits.ExceedsMaxDepth(document))
            {
                throw new NotSupportedException(
                    $"A document is nested deeper than {JsonLimits.MaxDepth} levels and cannot be diffed.");
            }
        }
    }

    private static void DiffObject(
        JsonObject? before,
        JsonObject? after,
        string? prefix,
        SortedDictionary<string, DiffEntry> entries)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (before is not null)
        {
            foreach (var (key, _) in before)
            {
                keys.Add(key);
            }
        }

        if (after is not null)
        {
            foreach (var (key, _) in after)
            {
                keys.Add(key);
            }
        }

        foreach (var key in keys)
        {
            // A '.' or '\' inside a key is escaped (DiffPath), so a dictionary key such as
            // "feature.enabled" is one segment and the path stays unambiguous; an empty key is an empty
            // segment ("" at the top, "a." below a).
            var path = DiffPath.Append(prefix, key);
            var hasOld = before?.ContainsKey(key) ?? false;
            var hasNew = after?.ContainsKey(key) ?? false;
            var oldValue = hasOld ? before![key] : null;
            var newValue = hasNew ? after![key] : null;

            if (hasOld && hasNew)
            {
                if (oldValue is JsonObject oldObject && newValue is JsonObject newObject)
                {
                    DiffObject(oldObject, newObject, path, entries);
                }
                else if (!JsonNode.DeepEquals(oldValue, newValue))
                {
                    entries[path] = DiffEntry.Changed(oldValue?.DeepClone(), newValue?.DeepClone());
                }
            }
            else if (hasOld)
            {
                entries[path] = DiffEntry.Removed(oldValue?.DeepClone());
            }
            else
            {
                entries[path] = DiffEntry.Added(newValue?.DeepClone());
            }
        }
    }

    private static JsonObject ApplyCore(JsonObject? source, DocumentDiff diff, bool useNew)
    {
        ArgumentNullException.ThrowIfNull(diff);
        EnsureDepth(source);

        var result = source?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var (path, entry) in diff.Entries)
        {
            if (entry.Kind != DiffEntryKind.Tracked)
            {
                // Redacted/Reference/Hashed entries carry no values (ADR-007) — state
                // reconstruction must fail loudly, never produce silently wrong data (ADR-008).
                throw new InvalidOperationException(
                    $"Diff entry '{path}' is {entry.Kind} and carries no values; " +
                    "the document state cannot be reconstructed from this diff.");
            }

            var present = useNew ? entry.HasNew : entry.HasOld;
            var value = useNew ? entry.New : entry.Old;
            var segments = DiffPath.Split(path);
            if (present)
            {
                NavigateToParent(result, segments)[segments[^1]] = value?.DeepClone();
            }
            else
            {
                // A removal never creates its parent: when a policy split moved a field below its container
                // (PolicyApplier), the container entry sorts first and may already have removed or replaced it.
                FindParent(result, segments)?.Remove(segments[^1]);
            }
        }

        return result;
    }

    private static JsonObject? FindParent(JsonObject root, string[] segments)
    {
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static JsonObject NavigateToParent(JsonObject root, string[] segments)
    {
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is JsonObject next)
            {
                current = next;
            }
            else
            {
                var created = new JsonObject();
                current[segments[i]] = created;
                current = created;
            }
        }

        return current;
    }
}
