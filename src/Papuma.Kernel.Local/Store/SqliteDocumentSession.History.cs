// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Document history reads — SQLite counterpart of <c>DocumentSession.History.cs</c>. The
/// public API behind the ADR-003 promise: on a concurrency conflict, the application can
/// load the intermediate diffs and build precise conflict UIs or audit views.
/// </summary>
public sealed partial class SqliteDocumentSession
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
            SELECT seq, version, schema_version, operation, diff, actor_id, metadata, occurred_at
            FROM change
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND document_id = @id
              AND (@fromVersion IS NULL OR version >= @fromVersion)
              AND (@toVersion IS NULL OR version <= @toVersion)
            ORDER BY version
            """;
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("fromVersion", (object?)fromVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("toVersion", (object?)toVersion ?? DBNull.Value);

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
                Operation: (ChangeOperation)reader.GetInt32(3),
                Diff: DocumentDiff.FromJson((JsonObject)JsonNode.Parse(reader.GetString(4))!),
                ActorId: reader.GetString(5),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(6))!,
                OccurredAt: DateTimeOffset.Parse(reader.GetString(7))));
        }

        return history;
    }

    /// <summary>
    /// Loads every change a unit of work produced — all records carrying
    /// <paramref name="correlationId"/>, across document types, in feed order (<c>seq</c>).
    /// The read side of "what did this command do?": an audit timeline entry, or a test
    /// asserting a command's complete effect. Scope-bound like every read; diffs are
    /// policy-applied (ADR-007). Sees the session's own uncommitted writes.
    /// </summary>
    /// <param name="correlationId">
    /// The correlation id shared by a session's writes — <see cref="CorrelationId"/> of the
    /// writing session, or <c>BulkResult.CorrelationId</c>.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<IReadOnlyList<ChangeRecord>> GetChangesByCorrelationAsync(
        Guid correlationId,
        CancellationToken ct = default)
    {
        var (conn, tx) = await EnsureTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT seq, document_type, document_id, version, schema_version, operation,
                   diff, actor_id, metadata, occurred_at
            FROM change
            WHERE scope = @scope AND tenant_id = @tenantId
              AND json_extract(metadata, '$.correlationId') = @correlationId
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("correlationId", correlationId.ToString("N"));

        var changes = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            changes.Add(new ChangeRecord(
                Seq: reader.GetInt64(0),
                Scope: Scope,
                DocumentType: reader.GetString(1),
                DocumentId: reader.GetString(2),
                Version: reader.GetInt64(3),
                SchemaVersion: reader.GetInt32(4),
                Operation: (ChangeOperation)reader.GetInt32(5),
                Diff: DocumentDiff.FromJson((JsonObject)JsonNode.Parse(reader.GetString(6))!),
                ActorId: reader.GetString(7),
                Metadata: (JsonObject)JsonNode.Parse(reader.GetString(8))!,
                OccurredAt: DateTimeOffset.Parse(reader.GetString(9))));
        }

        return changes;
    }
}
