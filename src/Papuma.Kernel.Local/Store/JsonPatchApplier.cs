// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Store;

/// <summary>
/// Applies patch operations (<c>Set</c>/<c>Remove</c>/<c>Increment</c>) to an in-memory
/// JSON document — the SQLite kernel's replacement for Postgres's nested
/// <c>jsonb_set</c>/<c>#-</c> SQL-expression composition (<c>BuildPatchExpression</c> in
/// the Postgres kernel's <c>DocumentSession.Patch.cs</c>).
/// </summary>
/// <remarks>
/// Behavior-equivalent, not just approximate: a single-writer store has no
/// concurrent-writer race for <c>Increment</c> to protect against mid-statement the way
/// Postgres's atomic SQL expression does (<c>jsonb_set(data, path, to_jsonb(COALESCE(...) + by))</c>
/// runs inside one statement specifically to close that window) — the whole patch here
/// already runs inside one exclusive SQLite transaction, so reading the current value in
/// .NET and adding to it is equally atomic in effect.
/// </remarks>
internal static class JsonPatchApplier
{
    public static void Apply(JsonObject target, IReadOnlyList<PatchOperation> operations)
    {
        foreach (var op in operations)
        {
            switch (op.Kind)
            {
                case PatchOperationKind.Set:
                    Set(target, op.PathSegments, op.Value?.DeepClone());
                    break;

                case PatchOperationKind.Remove:
                    Remove(target, op.PathSegments);
                    break;

                case PatchOperationKind.Increment:
                    Increment(target, op.PathSegments, op.IncrementBy);
                    break;
            }
        }
    }

    private static void Set(JsonObject root, IReadOnlyList<string> segments, JsonNode? value)
    {
        var parent = NavigateToParent(root, segments, createMissing: true)!;
        parent[segments[^1]] = value;
    }

    private static void Remove(JsonObject root, IReadOnlyList<string> segments)
    {
        var parent = NavigateToParent(root, segments, createMissing: false);
        parent?.Remove(segments[^1]);
    }

    private static void Increment(JsonObject root, IReadOnlyList<string> segments, long by)
    {
        var parent = NavigateToParent(root, segments, createMissing: true)!;
        var current = parent[segments[^1]] is JsonValue existing && existing.TryGetValue<decimal>(out var n)
            ? n
            : 0m;
        parent[segments[^1]] = JsonValue.Create(current + by);
    }

    /// <summary>
    /// Walks to the parent object of the last path segment, creating missing
    /// intermediate objects when <paramref name="createMissing"/> (matches
    /// <c>jsonb_set(..., create_missing := true)</c>), or returning <c>null</c> as soon
    /// as the path can't be followed (so <see cref="Remove"/> is a clean no-op on a
    /// missing path, matching <c>#-</c>'s behavior on a non-existent path).
    /// </summary>
    private static JsonObject? NavigateToParent(JsonObject root, IReadOnlyList<string> segments, bool createMissing)
    {
        JsonObject? current = root;
        for (var i = 0; i < segments.Count - 1 && current is not null; i++)
        {
            if (current[segments[i]] is JsonObject next)
            {
                current = next;
            }
            else if (createMissing)
            {
                var created = new JsonObject();
                current[segments[i]] = created;
                current = created;
            }
            else
            {
                current = null;
            }
        }

        return current;
    }
}
