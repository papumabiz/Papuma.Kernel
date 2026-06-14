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
