// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a stored document carries a higher schema version than the running
/// model — typically an old deployment touching documents already migrated by a newer
/// one. Failing loudly prevents silent back-migration (ADR-005, point 6).
/// </summary>
public sealed class SchemaVersionConflictException : Exception
{
    /// <summary>Gets the logical document type name.</summary>
    public string DocumentType { get; }

    /// <summary>Gets the document identifier.</summary>
    public string DocumentId { get; }

    /// <summary>Gets the schema version stored in the database.</summary>
    public int StoredSchemaVersion { get; }

    /// <summary>Gets the schema version of the running model.</summary>
    public int ModelSchemaVersion { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaVersionConflictException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="storedSchemaVersion">The schema version stored in the database.</param>
    /// <param name="modelSchemaVersion">The schema version of the running model.</param>
    public SchemaVersionConflictException(
        string documentType,
        string documentId,
        int storedSchemaVersion,
        int modelSchemaVersion)
        : base($"Document {documentType}/{documentId} is stored with schema version {storedSchemaVersion}, " +
               $"but the running model only knows version {modelSchemaVersion}. " +
               "A newer deployment has migrated this document — refusing to read or overwrite it (ADR-005).")
    {
        DocumentType = documentType;
        DocumentId = documentId;
        StoredSchemaVersion = storedSchemaVersion;
        ModelSchemaVersion = modelSchemaVersion;
    }
}
