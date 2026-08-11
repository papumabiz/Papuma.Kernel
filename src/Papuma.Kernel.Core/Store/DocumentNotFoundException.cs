// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when an update or delete targets a document that does not exist in the current scope.
/// </summary>
public sealed class DocumentNotFoundException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the document identifier.</summary>
    public string DocumentId { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentNotFoundException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier.</param>
    public DocumentNotFoundException(string documentType, string documentId)
        : base($"Document {documentType}/{documentId} does not exist in the current scope.")
    {
        DocumentType = documentType;
        DocumentId = documentId;
    }
}
