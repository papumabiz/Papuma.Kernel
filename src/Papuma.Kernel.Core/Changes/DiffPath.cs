// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The path form of a field in a diff, a policy, a key or a GDPR selector: object keys joined by
/// <c>.</c>, with a <c>.</c> or a <c>\</c> inside a key escaped as <c>\.</c> and <c>\\</c>
/// (<c>docs/spec/changes.md</c>, §2). The segment list is the model; the string is its serialization,
/// so a key such as <c>feature.enabled</c> in a dictionary is one segment, not two.
/// </summary>
/// <remarks>
/// A path without a dot or a backslash in any key — every path of a declared property — is
/// byte-for-byte what it was before escaping existed, so stored data and consumers that treat paths as
/// opaque strings are unaffected. Escaped paths sort as plain strings (ordinal), not segment by segment.
/// </remarks>
internal static class DiffPath
{
    /// <summary>Separates the segments of a path.</summary>
    public const char Separator = '.';

    /// <summary>Escapes a separator or itself inside a segment.</summary>
    public const char Escape = '\\';

    /// <summary>Returns <paramref name="segment"/> with <c>\</c> and <c>.</c> escaped.</summary>
    public static string EscapeSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.AsSpan().IndexOfAny(Separator, Escape) < 0)
        {
            return segment;
        }

        var builder = new StringBuilder(segment.Length + 2);
        foreach (var ch in segment)
        {
            if (ch is Separator or Escape)
            {
                builder.Append(Escape);
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>Appends one (unescaped) key to a path; an empty <paramref name="prefix"/> is the root.</summary>
    public static string Append(string prefix, string segment)
    {
        var escaped = EscapeSegment(segment);
        return prefix.Length == 0 ? escaped : $"{prefix}{Separator}{escaped}";
    }

    /// <summary>Joins (unescaped) segments into a path.</summary>
    public static string Join(IEnumerable<string> segments) =>
        string.Join(Separator, segments.Select(EscapeSegment));

    /// <summary>
    /// Splits a path into its (unescaped) segments.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty, has an empty segment, or a dangling or unknown escape.</exception>
    public static string[] Split(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var segments = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var ch = path[i];
            if (ch == Escape)
            {
                if (i + 1 >= path.Length || path[i + 1] is not (Separator or Escape))
                {
                    throw new ArgumentException(
                        $"Path '{path}' has an invalid escape at position {i}: only \\. and \\\\ are allowed.", nameof(path));
                }

                current.Append(path[++i]);
            }
            else if (ch == Separator)
            {
                segments.Add(Finish(current, path));
            }
            else
            {
                current.Append(ch);
            }
        }

        segments.Add(Finish(current, path));
        return [.. segments];
    }

    /// <summary>Returns the path of the parent (the path without its last segment), or <c>false</c> for a top-level path.</summary>
    public static bool TryGetParent(string path, out string parent)
    {
        var lastSeparator = -1;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == Escape)
            {
                i++; // skip the escaped character
            }
            else if (path[i] == Separator)
            {
                lastSeparator = i;
            }
        }

        parent = lastSeparator < 0 ? string.Empty : path[..lastSeparator];
        return lastSeparator >= 0;
    }

    private static string Finish(StringBuilder segment, string path)
    {
        if (segment.Length == 0)
        {
            throw new ArgumentException($"Path '{path}' has an empty segment.", nameof(path));
        }

        var result = segment.ToString();
        segment.Clear();
        return result;
    }
}
