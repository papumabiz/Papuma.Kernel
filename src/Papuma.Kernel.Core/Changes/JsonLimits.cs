// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The one structural limit of a document, everywhere: 64 levels of nested objects and arrays, the root
/// object counting as the first. It is what <c>System.Text.Json</c> enforces by default when the sessions
/// serialize a document and parse a stored one, so the engines (diff, canonical, policy projection, the UTF-8
/// prototype) accept exactly what the stores can write and read. For a <see cref="JsonNode"/> graph built by
/// other means the check keeps a recursive walk from ending in a stack overflow, which cannot be caught. The
/// check itself is iterative.
/// </summary>
internal static class JsonLimits
{
    /// <summary>The deepest nesting of objects and arrays a document may have; the serializer's and parser's default.</summary>
    public const int MaxDepth = 64;

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

    /// <summary>
    /// Whether a <see cref="JsonException"/> of the <i>serializer</i> means "too deep": it reports the depth (or a
    /// reference cycle, which it can only tell by its depth) with the path where it stopped, which then has
    /// <see cref="MaxDepth"/> segments. Read on the failure path only.
    /// </summary>
    public static bool IsSerializerDepthError(JsonException exception) =>
        exception.Path is { } path && Segments(path) >= MaxDepth;

    /// <summary>
    /// Whether UTF-8 JSON nests deeper than <see cref="MaxDepth"/> — the second look a failed parse gets, since the
    /// parser's exception does not say why it failed. Malformed input that fails before that depth is not too deep.
    /// </summary>
    public static bool TextExceedsMaxDepth(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = int.MaxValue });
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= MaxDepth)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // malformed before reaching the limit
        }

        return false;
    }

    /// <summary>Whether JSON text nests deeper than <see cref="MaxDepth"/>; see the UTF-8 overload.</summary>
    public static bool TextExceedsMaxDepth(string json) => TextExceedsMaxDepth(Encoding.UTF8.GetBytes(json));

    // Segments of a serializer path such as $.a.b[0]['c.d']: separators outside quoted names.
    private static int Segments(string path)
    {
        var count = 0;
        var quoted = false;
        foreach (var ch in path)
        {
            if (ch == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && ch is '.' or '[')
            {
                count++;
            }
        }

        return count;
    }
}
