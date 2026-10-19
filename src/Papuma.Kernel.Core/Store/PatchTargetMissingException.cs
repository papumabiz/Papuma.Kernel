// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a patch <c>Set</c> or <c>Increment</c> addresses a field whose parent object
/// does not exist (or is not an object). A patch never creates or replaces parents; the
/// whole patch is rolled back instead of silently doing nothing (ADR-012). <c>Remove</c>
/// is unaffected — removing something that is not there stays a no-op.
/// </summary>
public sealed class PatchTargetMissingException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the document identifier, if known.</summary>
    public string? DocumentId { get; }

    /// <summary>Gets the dot-separated path of the field the operation addressed, or <c>null</c> when the engine cannot tell (set-based patches on PostgreSQL).</summary>
    public string? FieldPath { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PatchTargetMissingException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier, if known.</param>
    /// <param name="fieldPath">The dot-separated path of the addressed field, if known.</param>
    /// <param name="innerException">The underlying engine error, if any.</param>
    public PatchTargetMissingException(
        string documentType,
        string? documentId,
        string? fieldPath,
        Exception? innerException = null)
        : base(
            $"Patch on {documentType}" +
            (documentId is null ? string.Empty : $"/{documentId}") +
            " cannot reach " + (fieldPath is null ? "a field" : $"field '{fieldPath}'") +
            ": a parent object along the path does not exist or is not an object. " +
            "Patches never create or replace parents; set the parent first (ADR-012).",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        DocumentType = documentType;
        DocumentId = documentId;
        FieldPath = fieldPath;
    }
}
