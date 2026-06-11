// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Scope-bound session for loading and writing documents (ADR-002/003).
/// </summary>
/// <remarks>
/// <para>
/// Every write is a single atomic statement using PostgreSQL 18 <c>RETURNING OLD/NEW</c>:
/// the previous state is captured in the same statement that changes the row — no prior
/// read, no race window. Document write and change record commit in one transaction.
/// </para>
/// <para>
/// Phase-2 scope: each call commits its own transaction; multi-write unit-of-work
/// semantics arrive in phase 6. The document type is derived from
/// <c>typeof(T).Name</c> until the metamodel lands in phase 3; the schema version is
/// fixed at 1 until upcasting lands in phase 4.
/// </para>
/// </remarks>
public sealed class DocumentSession
{
    private const int CurrentSchemaVersion = 1;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Gets the scope this session is bound to.</summary>
    public ScopeContext Scope { get; }

    internal DocumentSession(NpgsqlDataSource dataSource, ScopeContext scope)
    {
        _dataSource = dataSource;
        Scope = scope;
    }

    /// <summary>
    /// Loads a document by id, or returns <c>null</c> when it does not exist in this scope.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<DocumentResult<T>?> LoadAsync<T>(string id, CancellationToken ct = default)
        where T : class
    {
        var documentType = DocumentTypeOf<T>();
        InputValidator.ValidateDocumentId(id);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(Scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT data::text, version
            FROM papuma.document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND id = @id
            """;
        AddIdentityParameters(cmd, documentType, id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        var version = reader.GetInt64(1);
        var document = JsonSerializer.Deserialize<T>(json, KernelJson.Options)
            ?? throw new InvalidOperationException($"Document {documentType}/{id} deserialized to null.");

        return new DocumentResult<T>(document, version);
    }

    /// <summary>
    /// Saves a document with optimistic concurrency. Pass <paramref name="expectedVersion"/> 0
    /// to insert a new document; otherwise the stored version must match.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="document">The document to persist.</param>
    /// <param name="expectedVersion">The expected stored version (0 = insert).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ConcurrencyException">The stored version does not match.</exception>
    /// <exception cref="DocumentNotFoundException">An update targeted a missing document.</exception>
    public async Task<SaveResult> SaveAsync<T>(string id, T document, long expectedVersion, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        var documentType = DocumentTypeOf<T>();
        InputValidator.ValidateDocumentId(id);

        var newJson = JsonSerializer.SerializeToNode(document, KernelJson.Options) as JsonObject
            ?? throw new ArgumentException(
                $"Document of type {typeof(T).Name} must serialize to a JSON object.", nameof(document));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(Scope, ct);

        return expectedVersion == 0
            ? await InsertAsync(conn, tx, documentType, id, newJson, ct)
            : await UpdateAsync(conn, tx, documentType, id, newJson, expectedVersion, ct);
    }

    /// <summary>
    /// Deletes a document with optimistic concurrency.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="expectedVersion">The expected stored version.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ConcurrencyException">The stored version does not match.</exception>
    /// <exception cref="DocumentNotFoundException">The document does not exist.</exception>
    public async Task<SaveResult> DeleteAsync<T>(string id, long expectedVersion, CancellationToken ct = default)
        where T : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        var documentType = DocumentTypeOf<T>();
        InputValidator.ValidateDocumentId(id);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(Scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM papuma.document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND id = @id
              AND version = @expectedVersion
            RETURNING old.data::text
            """;
        AddIdentityParameters(cmd, documentType, id);
        cmd.Parameters.AddWithValue("expectedVersion", expectedVersion);

        var oldJsonText = (string?)await cmd.ExecuteScalarAsync(ct);
        if (oldJsonText is null)
        {
            throw await VersionConflictAsync(conn, tx, documentType, id, expectedVersion, ct);
        }

        var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
        var diff = JsonDiffEngine.Diff(oldJson, after: null);
        var deletedVersion = expectedVersion + 1;

        await InsertChangeRecordAsync(conn, tx, documentType, id, deletedVersion, ChangeOperation.Delete, diff, ct);
        await tx.CommitAsync(ct);

        return new SaveResult(deletedVersion, ChangeOperation.Delete, diff);
    }

    private async Task<SaveResult> InsertAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string documentType,
        string id,
        JsonObject newJson,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Version numbering continues after a delete (max change version + 1) so that
        // re-creating an id never collides with the gapless per-document change history.
        cmd.CommandText = """
            INSERT INTO papuma.document (scope, tenant_id, document_type, id, version, schema_version, data)
            SELECT @scope, @tenantId, @type, @id,
                   COALESCE((SELECT MAX(c.version)
                             FROM papuma.change c
                             WHERE c.scope = @scope AND c.tenant_id = @tenantId
                               AND c.document_type = @type AND c.document_id = @id), 0) + 1,
                   @schemaVersion, @data
            ON CONFLICT (scope, tenant_id, document_type, id) DO NOTHING
            RETURNING version
            """;
        AddIdentityParameters(cmd, documentType, id);
        cmd.Parameters.AddWithValue("schemaVersion", CurrentSchemaVersion);
        AddJsonbParameter(cmd, "data", newJson);

        var inserted = await cmd.ExecuteScalarAsync(ct);
        if (inserted is not long insertedVersion)
        {
            throw await VersionConflictAsync(conn, tx, documentType, id, expectedVersion: 0, ct);
        }

        var diff = JsonDiffEngine.Diff(before: null, newJson);
        await InsertChangeRecordAsync(conn, tx, documentType, id, insertedVersion, ChangeOperation.Insert, diff, ct);
        await tx.CommitAsync(ct);

        return new SaveResult(insertedVersion, ChangeOperation.Insert, diff);
    }

    private async Task<SaveResult> UpdateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string documentType,
        string id,
        JsonObject newJson,
        long expectedVersion,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // RETURNING OLD captures the replaced state atomically (ADR-003); NEW returns the
        // stored (jsonb-normalized) form so the diff reflects what is actually persisted.
        cmd.CommandText = """
            UPDATE papuma.document
            SET data = @data,
                version = version + 1,
                schema_version = @schemaVersion,
                updated_at = now()
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND id = @id
              AND version = @expectedVersion
            RETURNING old.data::text AS old_data, new.data::text AS new_data, new.version AS new_version
            """;
        AddIdentityParameters(cmd, documentType, id);
        cmd.Parameters.AddWithValue("schemaVersion", CurrentSchemaVersion);
        cmd.Parameters.AddWithValue("expectedVersion", expectedVersion);
        AddJsonbParameter(cmd, "data", newJson);

        string oldJsonText;
        string newJsonText;
        long newVersion;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                await reader.DisposeAsync();
                throw await VersionConflictAsync(conn, tx, documentType, id, expectedVersion, ct);
            }

            oldJsonText = reader.GetString(0);
            newJsonText = reader.GetString(1);
            newVersion = reader.GetInt64(2);
        }

        var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
        var storedNewJson = (JsonObject)JsonNode.Parse(newJsonText)!;
        var diff = JsonDiffEngine.Diff(oldJson, storedNewJson);

        await InsertChangeRecordAsync(conn, tx, documentType, id, newVersion, ChangeOperation.Update, diff, ct);
        await tx.CommitAsync(ct);

        return new SaveResult(newVersion, ChangeOperation.Update, diff);
    }

    private async Task InsertChangeRecordAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string documentType,
        string id,
        long version,
        ChangeOperation operation,
        DocumentDiff diff,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO papuma.change
                (scope, tenant_id, document_type, document_id, version, schema_version, operation, diff, metadata)
            VALUES
                (@scope, @tenantId, @type, @id, @version, @schemaVersion, @operation, @diff, '{}'::jsonb)
            """;
        AddIdentityParameters(cmd, documentType, id);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("schemaVersion", CurrentSchemaVersion);
        cmd.Parameters.AddWithValue("operation", (short)operation);
        AddJsonbParameter(cmd, "diff", diff.ToJson());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Builds the precise failure for a zero-row write: not found vs. version conflict
    /// (with the actual stored version, queried in the same transaction).
    /// </summary>
    private async Task<Exception> VersionConflictAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string documentType,
        string id,
        long expectedVersion,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT version
            FROM papuma.document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND id = @id
            """;
        AddIdentityParameters(cmd, documentType, id);

        var actual = await cmd.ExecuteScalarAsync(ct);
        if (actual is long actualVersion)
        {
            return new ConcurrencyException(documentType, id, expectedVersion, actualVersion);
        }

        return new DocumentNotFoundException(documentType, id);
    }

    private void AddIdentityParameters(NpgsqlCommand cmd, string documentType, string id)
    {
        cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("type", documentType);
        cmd.Parameters.AddWithValue("id", id);
    }

    private static void AddJsonbParameter(NpgsqlCommand cmd, string name, JsonNode json)
    {
        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = json.ToJsonString(),
        });
    }

    private static string DocumentTypeOf<T>()
    {
        var documentType = typeof(T).Name;
        InputValidator.ValidateDocumentType(documentType);
        return documentType;
    }
}
