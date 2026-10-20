// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.RegularExpressions;

namespace Papuma.Kernel.Validation;

/// <summary>
/// Provides shared input validation for document and change feed operations.
/// </summary>
public static class InputValidator
{
    private static readonly Regex ValidDocumentTypePattern = new(
        "^[A-Za-z][A-Za-z0-9_]{1,100}$",
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
    public static void ValidateDocumentType(string documentType)
    {
        if (documentType is null || !ValidDocumentTypePattern.IsMatch(documentType))
        {
            throw new ArgumentException(
                $"Invalid documentType '{documentType}'. Must match [A-Za-z][A-Za-z0-9_]{{1,100}}.",
                nameof(documentType));
        }
    }

    /// <summary>
    /// Validates a document identifier.
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
    }
}
