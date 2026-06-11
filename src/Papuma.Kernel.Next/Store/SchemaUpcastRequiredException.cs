// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when a patch targets a document stored with an older schema version.
/// Patches write raw JSON without materializing the document, so the upcaster chain
/// cannot run — lift the document first via Load + Save (ADR-012).
/// </summary>
public sealed class SchemaUpcastRequiredException : Exception
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
    /// Initializes a new instance of the <see cref="SchemaUpcastRequiredException"/> class.
    /// </summary>
    /// <param name="documentType">The logical document type name.</param>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="storedSchemaVersion">The schema version stored in the database.</param>
    /// <param name="modelSchemaVersion">The schema version of the running model.</param>
    public SchemaUpcastRequiredException(
        string documentType,
        string documentId,
        int storedSchemaVersion,
        int modelSchemaVersion)
        : base($"Document {documentType}/{documentId} is stored with schema version {storedSchemaVersion}, " +
               $"but the model is at version {modelSchemaVersion}. Patches cannot run the upcaster chain — " +
               "load and save the document first to lift it (ADR-012).")
    {
        DocumentType = documentType;
        DocumentId = documentId;
        StoredSchemaVersion = storedSchemaVersion;
        ModelSchemaVersion = modelSchemaVersion;
    }
}
