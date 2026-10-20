// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a patch <c>Increment</c> cannot be computed exactly by the in-process (SQLite) engine: the stored
/// number or the result lies outside the range of a 28-digit <see cref="decimal"/> (about ±7.9·10²⁸), or has more
/// digits than it can hold. The increment never rounds or truncates silently; the whole patch is rolled back.
/// PostgreSQL computes in <c>numeric</c>, which has no such limit — the documented divergence of
/// <c>docs/spec/patch.md</c> (§5).
/// </summary>
public sealed class PatchValueOutOfRangeException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the document identifier, or <c>null</c> when the engine cannot tell which document failed.</summary>
    public string? DocumentId { get; }

    /// <summary>Gets the dot-separated field path, or <c>null</c> when the engine cannot tell which field failed.</summary>
    public string? FieldPath { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PatchValueOutOfRangeException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier, if known.</param>
    /// <param name="fieldPath">The dot-separated field path, if known.</param>
    /// <param name="innerException">The underlying arithmetic error, if any.</param>
    public PatchValueOutOfRangeException(
        string documentType,
        string? documentId,
        string? fieldPath,
        Exception? innerException = null)
        : base(
            $"Increment on {documentType}" +
            (documentId is null ? string.Empty : $"/{documentId}") +
            (fieldPath is null ? string.Empty : $" field '{fieldPath}'") +
            " failed: the stored value or the result is outside the range this engine computes exactly " +
            "(a decimal of up to 28 significant digits, about ±7.9e28). Nothing was changed.",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        DocumentType = documentType;
        DocumentId = documentId;
        FieldPath = fieldPath;
    }
}
