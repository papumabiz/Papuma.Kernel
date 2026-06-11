// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a write violates a declared unique key (ADR-006).
/// </summary>
public sealed class UniqueKeyViolationException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the dot-separated JSON path of the violated key.</summary>
    public string KeyPath { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueKeyViolationException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="keyPath">The violated key path.</param>
    /// <param name="inner">The underlying PostgreSQL exception.</param>
    public UniqueKeyViolationException(string documentType, string keyPath, Exception inner)
        : base($"Unique key violation on {documentType}.{keyPath}: another document in this scope already carries this value.", inner)
    {
        DocumentType = documentType;
        KeyPath = keyPath;
    }
}
