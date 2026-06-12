// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Document history reads (phase 11) — the public API behind the ADR-003 promise:
/// on a concurrency conflict, the application can load the intermediate diffs and
/// build precise conflict UIs ("field X was changed in the meantime") or audit views.
/// </summary>
public sealed partial class DocumentSession
{
    /// <summary>
    /// Loads the change history of a document in ascending version order. Diffs are
    /// policy-applied (ADR-007) — sensitive values never appear. Sees the session's
    /// own uncommitted writes.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="fromVersion">Inclusive lower version bound (e.g. <c>expectedVersion + 1</c> in a conflict).</param>
    /// <param name="toVersion">Inclusive upper version bound.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<IReadOnlyList<ChangeRecord>> GetHistoryAsync<T>(
        string id,
        long? fromVersion = null,
        long? toVersion = null,
        CancellationToken ct = default)
        where T : class
    {
        var metadata = _model.GetRequired<T>();
        InputValidator.ValidateDocumentId(id);
        if (fromVersion is < 1 || toVersion is < 1 || toVersion < fromVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion),
                "Version bounds must be >= 1 and fromVersion <= toVersion.");
        }

        var (conn, tx) = await EnsureTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT seq, version, schema_version, operation, diff::text, metadata::text, occurred_at
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND document_id = @id
              AND (@fromVersion::bigint IS NULL OR version >= @fromVersion)
              AND (@toVersion::bigint IS NULL OR version <= @toVersion)
            ORDER BY version
            """;
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter<long?>("fromVersion", NpgsqlTypes.NpgsqlDbType.Bigint)
        {
            TypedValue = fromVersion,
        });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter<long?>("toVersion", NpgsqlTypes.NpgsqlDbType.Bigint)
        {
            TypedValue = toVersion,
        });

        var history = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            history.Add(new ChangeRecord(
                Seq: reader.GetInt64(0),
                Scope: Scope,
                DocumentType: metadata.Name,
                DocumentId: id,
                Version: reader.GetInt64(1),
                SchemaVersion: reader.GetInt32(2),
                Operation: (ChangeOperation)reader.GetInt16(3),
                Diff: DocumentDiff.FromJson((JsonObject)JsonNode.Parse(reader.GetString(4))!),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(5))!,
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return history;
    }
}
