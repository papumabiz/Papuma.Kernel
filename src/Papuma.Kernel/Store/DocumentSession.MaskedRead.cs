// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Policy-projected (masked) reads (ADR-016): load a document with field policies
/// applied to its current values, so sensitive fields never leave in clear text.
/// The clear-text <see cref="LoadAsync{T}"/> remains the in-process path for business
/// logic; the masked read is the variant for AI agents and support tooling.
/// </summary>
public sealed partial class DocumentSession
{
    /// <summary>
    /// Loads a document by id with field policies applied (ADR-016), or <c>null</c> when
    /// it does not exist in this scope. The result is masked JSON, not a typed document:
    /// <see cref="FieldPolicy.Redact"/>/<see cref="FieldPolicy.Reference"/> fields show a
    /// placeholder, <see cref="FieldPolicy.Hash"/> fields their hash,
    /// <see cref="FieldPolicy.DoNotTrack"/> fields are omitted.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<MaskedDocumentResult?> LoadMaskedAsync<T>(string id, CancellationToken ct = default)
        where T : class =>
        LoadMaskedCoreAsync(_model.GetRequired<T>(), id, ct);

    /// <summary>
    /// MCP entry point: masked read by id, resolving the type by its logical name and
    /// enforcing the <c>ExposeToMcp()</c> opt-in (ADR-016).
    /// </summary>
    internal Task<MaskedDocumentResult?> LoadMaskedForMcpAsync(
        string documentType, string id, CancellationToken ct) =>
        LoadMaskedCoreAsync(ResolveExposedType(documentType), id, ct);

    /// <summary>
    /// MCP entry point: masked read by a declared key (ADR-006/016). The key path must
    /// be a declared key — there is no free-form query (ADR-009).
    /// </summary>
    internal async Task<MaskedDocumentResult?> LoadMaskedByKeyForMcpAsync(
        string documentType, string keyPath, string value, CancellationToken ct)
    {
        var metadata = ResolveExposedType(documentType);
        var key = metadata.Keys.FirstOrDefault(k => k.Path == keyPath)
            ?? throw new ArgumentException(
                $"'{keyPath}' is not a declared key on {metadata.Name}. " +
                "Masked reads are limited to declared keys — there is no free-form query (ADR-009).",
                nameof(keyPath));

        var (conn, tx) = await EnsureTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT data::text, version, schema_version
            FROM papuma.document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type
              AND data #>> @path = @value
            LIMIT 2
            """;
        AddScopeParameters(cmd, metadata.Name);
        cmd.Parameters.AddWithValue("path", key.PathSegments);
        cmd.Parameters.AddWithValue("value", value);

        var matches = new List<MaskedDocumentResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            matches.Add(MaskRow(metadata, reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Key {metadata.Name}.{keyPath} matched multiple documents. Use a unique key."),
        };
    }

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
            SELECT data::text, version, schema_version
            FROM papuma.document
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
    private static MaskedDocumentResult MaskRow(
        DocumentTypeMetadata metadata, string json, long version, int storedSchemaVersion)
    {
        EnsureSchemaNotNewer(metadata, "(masked read)", storedSchemaVersion);
        var raw = (JsonObject)JsonNode.Parse(json)!;
        if (storedSchemaVersion < metadata.SchemaVersion)
        {
            metadata.Upcast(raw, storedSchemaVersion);
        }

        return new MaskedDocumentResult(PolicyProjector.Apply(raw, metadata), version);
    }
}
