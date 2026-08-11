// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a rollback target cannot be reconstructed because a diff on the way
/// back contains policy entries without values (Redacted/Reference/Hashed, ADR-007).
/// The kernel never writes silently wrong state (ADR-008).
/// </summary>
public sealed class RollbackNotPossibleException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the document identifier.</summary>
    public string DocumentId { get; }

    /// <summary>Gets the diff path that blocks reconstruction.</summary>
    public string Path { get; }

    /// <summary>Gets the policy shape of the blocking entry.</summary>
    public DiffEntryKind Kind { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RollbackNotPossibleException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="path">The diff path that blocks reconstruction.</param>
    /// <param name="kind">The policy shape of the blocking entry.</param>
    public RollbackNotPossibleException(string documentType, string documentId, string path, DiffEntryKind kind)
        : base($"Cannot roll back {documentType}/{documentId}: the change history contains a {kind} entry " +
               $"at '{path}' that carries no values (ADR-007). Restore the field from its reference source " +
               "or roll back manually — the kernel never reconstructs silently wrong state (ADR-008).")
    {
        DocumentType = documentType;
        DocumentId = documentId;
        Path = path;
        Kind = kind;
    }
}
