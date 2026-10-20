// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.RegularExpressions;

namespace Papuma.Kernel.Validation;

/// <summary>
/// Provides shared input validation for document and change feed operations.
/// </summary>
public static class InputValidator
{
    // \z, not $: a trailing newline must not pass.
    private static readonly Regex ValidDocumentTypePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{1,100}\z",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>The longest JSON path segment (one property name) that may reach SQL text.</summary>
    internal const int MaxJsonPathSegmentLength = 101;

    // One property name of a key path: unlike a type name it may be a single letter or start with '_'.
    // \z, not $: a trailing newline must not pass.
    private static readonly Regex ValidJsonPathSegmentPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,100}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    // Index names are built lower-case and are at most 63 characters (PostgreSQL's identifier limit).
    private static readonly Regex ValidIndexNamePattern = new(
        @"^[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Validates one segment of a JSON path before it is interpolated into SQL text (a key index expression, a
    /// SQLite JSON path). The pattern is the whole defence there, so it is strict: letters, digits and
    /// <c>_</c>, not starting with a digit.
    /// </summary>
    /// <param name="segment">The path segment, i.e. the serialized name of one property.</param>
    /// <exception cref="ArgumentException">The segment is not a plain identifier.</exception>
    internal static void ValidateJsonPathSegment(string segment)
    {
        if (segment is null || !ValidJsonPathSegmentPattern.IsMatch(segment))
        {
            throw new ArgumentException(
                $"Invalid JSON path segment '{segment}'. A key can only be declared on a property whose serialized " +
                "name matches [A-Za-z_][A-Za-z0-9_]{0,100} — the name goes into the index definition as text.",
                nameof(segment));
        }
    }

    /// <summary>
    /// Validates an index name before it is interpolated into DDL.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <exception cref="ArgumentException">The name is not a lower-case identifier of at most 63 characters.</exception>
    internal static void ValidateIndexName(string indexName)
    {
        if (indexName is null || !ValidIndexNamePattern.IsMatch(indexName))
        {
            throw new ArgumentException(
                $"Invalid index name '{indexName}'. Must match [a-z][a-z0-9_]{{0,62}}.", nameof(indexName));
        }
    }

    /// <summary>
    /// Validates a logical document type name against the allowed pattern.
    /// </summary>
    /// <param name="documentType">The document type name to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the document type name is invalid.</exception>
    public static void ValidateDocumentType(string documentType) =>
        ValidateTypeName(documentType, nameof(documentType));

    /// <summary>
    /// Validates a logical type name (document or event) and blames <paramref name="parameterName"/> — the
    /// parameter the caller actually passed it in (review Q-4).
    /// </summary>
    /// <param name="name">The type name to validate.</param>
    /// <param name="parameterName">The name of the caller's parameter that held <paramref name="name"/>.</param>
    /// <exception cref="ArgumentException">The name does not match the pattern.</exception>
    internal static void ValidateTypeName(string name, string parameterName)
    {
        if (name is null || !ValidDocumentTypePattern.IsMatch(name))
        {
            throw new ArgumentException(
                $"Invalid {parameterName} '{name}'. Must match [A-Za-z][A-Za-z0-9_]{{1,100}}.",
                parameterName);
        }
    }

    /// <summary>
    /// Validates a document identifier: not empty or blank, at most 200 characters, and free of control
    /// characters (NUL, line breaks, tabs, DEL, the C1 range). Any other text is legal, including <c>/</c>:
    /// the kernel itself never parses a <c>{Type}/{id}/{path}</c> reference back into its parts.
    /// </summary>
    /// <param name="documentId">The document identifier to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the document identifier is invalid.</exception>
    public static void ValidateDocumentId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || documentId.Length > 200)
        {
            throw new ArgumentException(
                "documentId must not be empty and max 200 characters.",
                nameof(documentId));
        }

        // PostgreSQL rejects NUL in text only at the wire; others would reach logs, references and URLs raw (review C-10).
        foreach (var ch in documentId)
        {
            if (char.IsControl(ch))
            {
                throw new ArgumentException(
                    $"documentId must not contain control characters (found U+{(int)ch:X4}).",
                    nameof(documentId));
            }
        }
    }
}
