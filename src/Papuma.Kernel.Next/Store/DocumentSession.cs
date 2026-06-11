// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
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
/// read, no race window. Document write and change record commit in one transaction,
/// and every diff passes policy application (ADR-007) before reaching the feed.
/// </para>
/// <para>
/// Phase-3 scope: each call commits its own transaction; multi-write unit-of-work
/// semantics arrive in phase 6. The schema version is fixed per type until upcasting
/// lands in phase 4.
/// </para>
/// </remarks>
public sealed class DocumentSession
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly KernelModel _model;

    /// <summary>Gets the scope this session is bound to.</summary>
    public ScopeContext Scope { get; }

    internal DocumentSession(NpgsqlDataSource dataSource, KernelModel model, ScopeContext scope)
    {
        _dataSource = dataSource;
        _model = model;
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
        var metadata = _model.GetRequired<T>();
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
        AddIdentityParameters(cmd, metadata.Name, id);

        return await ReadSingleAsync<T>(cmd, metadata.Name, ct);
    }

    /// <summary>
    /// Loads a document by a declared key (ADR-006), or returns <c>null</c> when no
    /// document matches. Throws when the key matches more than one document — declare
    /// the key unique if single-match semantics are required.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="key">The key property (must be declared via attribute or fluent config).</param>
    /// <param name="value">The key value to match.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<DocumentResult<T>?> LoadByKeyAsync<T>(
        Expression<Func<T, object?>> key,
        object value,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var metadata = _model.GetRequired<T>();
        var path = JsonPathResolver.Resolve(key);
        var keyMetadata = metadata.Keys.FirstOrDefault(k => k.Path == path)
            ?? throw new ArgumentException(
                $"'{path}' is not a declared key on {metadata.Name}. " +
                "Declare it via [UniqueKey]/[LookupKey] or UniqueKey()/LookupKey() (ADR-006).",
                nameof(key));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(Scope, ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT data::text, version
            FROM papuma.document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type
              AND data #>> @path = @value
            LIMIT 2
            """;
        cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("type", metadata.Name);
        cmd.Parameters.AddWithValue("path", keyMetadata.PathSegments);
        cmd.Parameters.AddWithValue("value", ToKeyText(value));

        var results = new List<DocumentResult<T>>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var document = JsonSerializer.Deserialize<T>(reader.GetString(0), KernelJson.Options)
                    ?? throw new InvalidOperationException($"Document of type {metadata.Name} deserialized to null.");
                results.Add(new DocumentResult<T>(document, reader.GetInt64(1)));
            }
        }

        return results.Count switch
        {
            0 => null,
            1 => results[0],
            _ => throw new InvalidOperationException(
                $"Key {metadata.Name}.{path} matched multiple documents. " +
                "Use a unique key for single-match lookups."),
        };
    }

    /// <summary>
    /// Saves a document with optimistic concurrency. Pass <paramref name="expectedVersion"/> 0
    /// to insert a new document; otherwise the stored version must match. The document id
    /// is taken from the document itself (metamodel id property).
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="document">The document to persist.</param>
    /// <param name="expectedVersion">The expected stored version (0 = insert).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ConcurrencyException">The stored version does not match.</exception>
    /// <exception cref="DocumentNotFoundException">An update targeted a missing document.</exception>
    /// <exception cref="UniqueKeyViolationException">A declared unique key is violated.</exception>
    public async Task<SaveResult> SaveAsync<T>(T document, long expectedVersion, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

        var metadata = _model.GetRequired<T>();
        var id = metadata.GetDocumentId(document);
        InputValidator.ValidateDocumentId(id);

        var newJson = JsonSerializer.SerializeToNode(document, KernelJson.Options) as JsonObject
            ?? throw new ArgumentException(
                $"Document of type {typeof(T).Name} must serialize to a JSON object.", nameof(document));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(Scope, ct);

        return expectedVersion == 0
            ? await InsertAsync(conn, tx, metadata, id, newJson, ct)
            : await UpdateAsync(conn, tx, metadata, id, newJson, expectedVersion, ct);
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
        var metadata = _model.GetRequired<T>();
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
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("expectedVersion", expectedVersion);

        var oldJsonText = (string?)await cmd.ExecuteScalarAsync(ct);
        if (oldJsonText is null)
        {
            throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion, ct);
        }

        var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
        var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, after: null), metadata, id);
        var deletedVersion = expectedVersion + 1;

        await InsertChangeRecordAsync(conn, tx, metadata, id, deletedVersion, ChangeOperation.Delete, diff, ct);
        await tx.CommitAsync(ct);

        return new SaveResult(deletedVersion, ChangeOperation.Delete, diff);
    }

    private async Task<SaveResult> InsertAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DocumentTypeMetadata metadata,
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
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("schemaVersion", metadata.SchemaVersion);
        AddJsonbParameter(cmd, "data", newJson);

        var inserted = await ExecuteMappingKeyViolationsAsync(
            () => cmd.ExecuteScalarAsync(ct), metadata.Name);
        if (inserted is not long insertedVersion)
        {
            throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion: 0, ct);
        }

        var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(before: null, newJson), metadata, id);
        await InsertChangeRecordAsync(conn, tx, metadata, id, insertedVersion, ChangeOperation.Insert, diff, ct);
        await tx.CommitAsync(ct);

        return new SaveResult(insertedVersion, ChangeOperation.Insert, diff);
    }

    private async Task<SaveResult> UpdateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DocumentTypeMetadata metadata,
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
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("schemaVersion", metadata.SchemaVersion);
        cmd.Parameters.AddWithValue("expectedVersion", expectedVersion);
        AddJsonbParameter(cmd, "data", newJson);

        string oldJsonText;
        string newJsonText;
        long newVersion;
        var row = await ExecuteMappingKeyViolationsAsync(
            async () =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    return ((string, string, long)?)null;
                }

                return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
            },
            metadata.Name);

        if (row is null)
        {
            throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion, ct);
        }

        (oldJsonText, newJsonText, newVersion) = row.Value;

        var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
        var storedNewJson = (JsonObject)JsonNode.Parse(newJsonText)!;
        var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, storedNewJson), metadata, id);

        await InsertChangeRecordAsync(conn, tx, metadata, id, newVersion, ChangeOperation.Update, diff, ct);
        await tx.CommitAsync(ct);

        return new SaveResult(newVersion, ChangeOperation.Update, diff);
    }

    private async Task InsertChangeRecordAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DocumentTypeMetadata metadata,
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
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("schemaVersion", metadata.SchemaVersion);
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

    /// <summary>
    /// Executes a write and maps PostgreSQL unique violations (23505) on declared key
    /// indexes to <see cref="UniqueKeyViolationException"/>. Other violations bubble up.
    /// </summary>
    private async Task<TResult> ExecuteMappingKeyViolationsAsync<TResult>(
        Func<Task<TResult>> execute,
        string documentType)
    {
        try
        {
            return await execute();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            var match = _model.FindKeyByIndexName(ex.ConstraintName);
            if (match is not null)
            {
                throw new UniqueKeyViolationException(documentType, match.Value.Key.Path, ex);
            }

            throw;
        }
    }

    private static async Task<DocumentResult<T>?> ReadSingleAsync<T>(
        NpgsqlCommand cmd, string documentType, CancellationToken ct)
        where T : class
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        var version = reader.GetInt64(1);
        var document = JsonSerializer.Deserialize<T>(json, KernelJson.Options)
            ?? throw new InvalidOperationException($"Document of type {documentType} deserialized to null.");

        return new DocumentResult<T>(document, version);
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

    /// <summary>
    /// Converts a key value to the text form produced by the <c>#&gt;&gt;</c> operator.
    /// </summary>
    private static string ToKeyText(object value)
    {
        if (value is string s)
        {
            return s;
        }

        var node = JsonSerializer.SerializeToNode(value, KernelJson.Options)
            ?? throw new ArgumentException("Key value serialized to null.", nameof(value));
        return node is JsonValue jv && jv.TryGetValue<string>(out var text) ? text : node.ToJsonString();
    }
}
