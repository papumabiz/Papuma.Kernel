// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Policy-projected (masked) reads (ADR-016) — SQLite counterpart of
/// <c>DocumentSession.MaskedRead.cs</c>. Load a document with field policies applied to
/// its current values, so sensitive fields never leave in clear text. The clear-text
/// <see cref="SqliteDocumentSession.LoadAsync{T}"/> remains the in-process path for
/// business logic; the masked read is the variant for AI agents and support tooling.
/// </summary>
public sealed partial class SqliteDocumentSession
{
    /// <summary>
    /// Loads a document by id with field policies applied (ADR-016), or <c>null</c> when
    /// it does not exist in this scope. The result is masked JSON, not a typed document.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<MaskedDocumentResult?> LoadMaskedAsync<T>(string id, CancellationToken ct = default)
        where T : class =>
        LoadMaskedCoreAsync(_model.GetRequired<T>(), id, ct);

    /// <summary>
    /// MCP-style entry point: masked read by id, resolving the type by its logical name
    /// and enforcing the <c>ExposeToMcp()</c> opt-in (ADR-016).
    /// </summary>
    internal Task<MaskedDocumentResult?> LoadMaskedForMcpAsync(
        string documentType, string id, CancellationToken ct) =>
        LoadMaskedCoreAsync(ResolveExposedType(documentType), id, ct);

    // There is deliberately no masked read by key here. The PostgreSQL kernel has one for the MCP
    // tools (text comparison via #>>); a copy that compared json_extract against a TEXT parameter
    // could not find numeric or boolean keys (review L-4/L-19), and nothing called it — the MCP
    // package reaches only the PostgreSQL session. When Local gets MCP support, build it on the
    // same text semantics as RedactEventsAsync (PayloadTextAtPath) and test it against PostgreSQL.

    private DocumentTypeMetadata ResolveExposedType(string documentType)
    {
        var metadata = _model.DocumentTypes.FirstOrDefault(m => m.Name == documentType)
            ?? throw new ArgumentException(
                $"Document type '{documentType}' is not registered in the model.", nameof(documentType));
        if (!metadata.ExposedToMcp)
        {
            throw new InvalidOperationException(
                $"Document type '{documentType}' is not exposed for masked reads. " +
                "Opt in with ExposeToMcp() in the model if this is intended (ADR-016).");
        }

        return metadata;
    }

    private async Task<MaskedDocumentResult?> LoadMaskedCoreAsync(
        DocumentTypeMetadata metadata, string id, CancellationToken ct)
    {
        InputValidator.ValidateDocumentId(id);

        var (conn, tx) = await EnsureTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT data, version, schema_version
            FROM document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND id = @id
            """;
        AddIdentityParameters(cmd, metadata.Name, id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return MaskRow(metadata, reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2));
    }

    /// <summary>
    /// Upcasts the stored JSON to the current schema (lazy, ADR-005), then applies the
    /// read projection. Upcasting keeps the masked shape consistent with what the
    /// in-process <see cref="LoadAsync{T}"/> would deserialize.
    /// </summary>
    private MaskedDocumentResult MaskRow(
        DocumentTypeMetadata metadata, string json, long version, int storedSchemaVersion)
    {
        EnsureSchemaNotNewer(metadata, "(masked read)", storedSchemaVersion);
        var raw = (JsonObject)JsonNode.Parse(json)!;
        if (storedSchemaVersion < metadata.SchemaVersion)
        {
            metadata.Upcast(raw, storedSchemaVersion);
        }

        return new MaskedDocumentResult(PolicyProjector.Apply(raw, metadata, _hashKeys, Scope), version);
    }
}
