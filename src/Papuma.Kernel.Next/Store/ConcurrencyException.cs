// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a write expected a document version that no longer matches the stored
/// version — another writer changed or created the document in the meantime (ADR-003).
/// </summary>
/// <remarks>
/// Detection is the kernel's job, resolution is the application's: reload and retry,
/// surface the conflict to the user, or merge. The intermediate diffs between
/// <see cref="ExpectedVersion"/> and <see cref="ActualVersion"/> are available in the
/// change feed for precise conflict UIs (ADR-004).
/// </remarks>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the document identifier.</summary>
    public string DocumentId { get; }

    /// <summary>Gets the version the caller expected (0 = expected the document not to exist).</summary>
    public long ExpectedVersion { get; }

    /// <summary>Gets the version actually stored at the time of the conflict.</summary>
    public long ActualVersion { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrencyException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="expectedVersion">The version the caller expected.</param>
    /// <param name="actualVersion">The version actually stored.</param>
    public ConcurrencyException(string documentType, string documentId, long expectedVersion, long actualVersion)
        : base($"Concurrency conflict on {documentType}/{documentId}: expected version {expectedVersion}, " +
               $"but the stored version is {actualVersion}.")
    {
        DocumentType = documentType;
        DocumentId = documentId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
