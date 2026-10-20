// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Store;

/// <summary>
/// A document, or a value written into one, is nested deeper than <see cref="MaxDepth"/> levels of objects and
/// arrays (the root object counting as the first) — the one structural limit of the kernel (spec changes.md §2.5).
/// The same exception whether the depth shows up while serializing a document, applying a patch, parsing a stored
/// state or diffing a hand-built node graph; a serialized document with a reference cycle is reported the same
/// way, since the serializer can only tell it by its depth.
/// </summary>
/// <remarks>
/// Derives from <see cref="NotSupportedException"/>, which the engines threw for this case before.
/// A write that fails with it changes nothing.
/// </remarks>
public sealed class DocumentTooDeepException : NotSupportedException
{
    /// <summary>The deepest nesting a document may have.</summary>
    public const int MaxDepth = JsonLimits.MaxDepth;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="documentType">The logical document type, when known.</param>
    /// <param name="documentId">The document id, when known.</param>
    /// <param name="detail">What was being done, e.g. <c>"cannot be diffed"</c>.</param>
    /// <param name="innerException">The serializer's or parser's exception, when it found the depth.</param>
    public DocumentTooDeepException(
        string? documentType,
        string? documentId,
        string? detail = null,
        Exception? innerException = null)
        : base(
            (documentType is null ? "A document" : $"{documentType}" + (documentId is null ? string.Empty : $"/{documentId}")) +
            $" is nested deeper than {MaxDepth} levels of objects and arrays" +
            (detail is null ? "." : $" and {detail}.") +
            " This is the kernel's structural limit; a value serialized with a reference cycle exceeds it too.",
            innerException)
    {
        DocumentType = documentType;
        DocumentId = documentId;
    }

    /// <summary>Gets the logical document type, when known.</summary>
    public string? DocumentType { get; }

    /// <summary>Gets the document id, when known.</summary>
    public string? DocumentId { get; }
}
