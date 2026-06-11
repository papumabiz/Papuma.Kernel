// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

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
        var entries = new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal);
        DiffObject(before, after, prefix: string.Empty, entries);
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

    private static void DiffObject(
        JsonObject? before,
        JsonObject? after,
        string prefix,
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
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
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

        var result = source?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var (path, entry) in diff.Entries)
        {
            var present = useNew ? entry.HasNew : entry.HasOld;
            var value = useNew ? entry.New : entry.Old;
            var segments = path.Split('.');
            var parent = NavigateToParent(result, segments);

            if (present)
            {
                parent[segments[^1]] = value?.DeepClone();
            }
            else
            {
                parent.Remove(segments[^1]);
            }
        }

        return result;
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
