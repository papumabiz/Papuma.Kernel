// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a patch <c>Increment</c> targets a value that is neither absent, JSON
/// <c>null</c> nor a JSON number (a string, boolean, object or array). The increment
/// never overwrites such a value; the whole patch is rolled back (ADR-012).
/// </summary>
public sealed class NonNumericIncrementException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the document identifier, or <c>null</c> when the engine cannot tell which document failed (set-based patches on PostgreSQL).</summary>
    public string? DocumentId { get; }

    /// <summary>Gets the dot-separated field path, or <c>null</c> when the engine cannot tell which field failed.</summary>
    public string? FieldPath { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NonNumericIncrementException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier, if known.</param>
    /// <param name="fieldPath">The dot-separated field path, if known.</param>
    /// <param name="innerException">The underlying engine error, if any.</param>
    public NonNumericIncrementException(
        string documentType,
        string? documentId,
        string? fieldPath,
        Exception? innerException = null)
        : base(
            $"Increment on {documentType}" +
            (documentId is null ? string.Empty : $"/{documentId}") +
            (fieldPath is null ? string.Empty : $" field '{fieldPath}'") +
            " failed: the stored value is not a JSON number. Increment never overwrites a non-numeric value (ADR-012).",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        DocumentType = documentType;
        DocumentId = documentId;
        FieldPath = fieldPath;
    }
}
