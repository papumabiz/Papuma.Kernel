// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The structural limit of the JSON the diff, canonical and policy engines walk recursively. The
/// serializer already stops documents at 64 levels, so no document built from a POCO or parsed from text
/// gets near this; the limit exists for a <see cref="JsonNode"/> graph built by other means, where a
/// recursive walk would otherwise end in a stack overflow — which cannot be caught and takes the process
/// with it. The check itself is iterative.
/// </summary>
internal static class JsonLimits
{
    /// <summary>The deepest nesting of objects and arrays the engines accept, twice the serializer's default.</summary>
    public const int MaxDepth = 128;

    /// <summary>Returns whether <paramref name="root"/> nests objects or arrays deeper than <see cref="MaxDepth"/>.</summary>
    public static bool ExceedsMaxDepth(JsonNode? root)
    {
        if (root is null)
        {
            return false;
        }

        var stack = new Stack<(JsonNode Node, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            switch (node)
            {
                case JsonObject obj:
                    if (depth > MaxDepth)
                    {
                        return true;
                    }

                    foreach (var (_, child) in obj)
                    {
                        if (child is not null)
                        {
                            stack.Push((child, depth + 1));
                        }
                    }

                    break;

                case JsonArray array:
                    if (depth > MaxDepth)
                    {
                        return true;
                    }

                    foreach (var child in array)
                    {
                        if (child is not null)
                        {
                            stack.Push((child, depth + 1));
                        }
                    }

                    break;
            }
        }

        return false;
    }
}
