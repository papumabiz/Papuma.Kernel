// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Partial updates (ADR-012) and set-based bulk operations (ADR-014) — SQLite
/// counterpart of <c>DocumentSession.Patch.cs</c>. Patch application itself happens
/// in-process (<see cref="JsonPatchApplier"/>) rather than as generated SQL — see that
/// type's remarks for why this is behavior-equivalent, not an approximation.
/// </summary>
public sealed partial class SqliteDocumentSession
{
    /// <summary>
    /// Applies a partial update. Diff, policies and change record work exactly like a
    /// full save.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="patch">The patch operations (<c>Set</c>/<c>Remove</c>/<c>Increment</c>).</param>
    /// <param name="expectedVersion">
    /// Optional optimistic concurrency check. Without it, the patch is deliberate
    /// field-level last-writer-wins — correct for independent fields (ADR-012).
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="DocumentNotFoundException">The document does not exist.</exception>
    /// <exception cref="ConcurrencyException">The stored version does not match <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="SchemaUpcastRequiredException">The document is stored with an older schema version.</exception>
    public async Task<SaveResult> PatchAsync<T>(
        string id,
        Action<PatchBuilder<T>> patch,
        long? expectedVersion = null,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(patch);
        var metadata = _model.GetRequired<T>();
        InputValidator.ValidateDocumentId(id);
        var operations = CollectOperations(patch);

        using var activity = StartWriteActivity("patch", metadata.Name, id);
        try
        {
            return await ExecuteWriteAsync(async (conn, tx) =>
            {
                string oldJsonText;
                int oldSchemaVersion;
                await using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = """
                        SELECT data, schema_version FROM document
                        WHERE scope = @scope AND tenant_id = @tenantId AND document_type = @type AND id = @id
                        """;
                    AddIdentityParameters(selectCmd, metadata.Name, id);
                    await using var reader = await selectCmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                    {
                        throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion ?? 0, ct);
                    }

                    oldJsonText = reader.GetString(0);
                    oldSchemaVersion = reader.GetInt32(1);
                }

                EnsurePatchableSchema(metadata, id, oldSchemaVersion);

                var patchedJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
                JsonPatchApplier.Apply(patchedJson, operations);
                RunValidator(metadata, patchedJson);

                await using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                var versionPredicate = expectedVersion is null ? string.Empty : "AND version = @expectedVersion";
                updateCmd.CommandText = $"""
                    UPDATE document
                    SET data = @data, version = version + 1, updated_by = @actorId, updated_at = @now
                    WHERE scope = @scope AND tenant_id = @tenantId AND document_type = @type AND id = @id
                      {versionPredicate}
                    RETURNING version
                    """;
                AddIdentityParameters(updateCmd, metadata.Name, id);
                updateCmd.Parameters.AddWithValue("actorId", ActorId);
                updateCmd.Parameters.AddWithValue("data", patchedJson.ToJsonString());
                updateCmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
                if (expectedVersion is not null)
                {
                    updateCmd.Parameters.AddWithValue("expectedVersion", expectedVersion.Value);
                }

                var newVersion = await ExecuteMappingKeyViolationsAsync(
                    () => updateCmd.ExecuteScalarAsync(ct), metadata.Name);
                if (newVersion is not long version)
                {
                    throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion ?? 0, ct);
                }

                var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
                var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, patchedJson), metadata, id);

                await InsertChangeRecordAsync(
                    conn, tx, metadata, id, version, ChangeOperation.Update, diff, metadata.SchemaVersion, ct);

                return new SaveResult(version, ChangeOperation.Update, diff)
                {
                    Stored = (patchedJson, metadata.ClrType),
                };
            }, ct);
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
    }

    /// <summary>
    /// Applies the same patch to an explicit list of documents — one change record per
    /// affected document, all carrying the session's correlation id (ADR-014).
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="ids">The document identifiers.</param>
    /// <param name="patch">The patch operations.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<BulkResult> PatchManyAsync<T>(
        IReadOnlyCollection<string> ids,
        Action<PatchBuilder<T>> patch,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            InputValidator.ValidateDocumentId(id);
        }

        return BulkPatchAsync(
            patch,
            "AND id IN (SELECT value FROM json_each(@ids))",
            cmd => cmd.Parameters.AddWithValue("ids", JsonSerializer.Serialize(ids)),
            ct);
    }

    /// <summary>
    /// Applies the same patch to all documents matching a declared key (ADR-006/014).
    /// Predicates beyond key equality are deliberately unsupported — select ids yourself
    /// and use <see cref="PatchManyAsync{T}"/>.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="key">The declared key property.</param>
    /// <param name="value">The key value to match.</param>
    /// <param name="patch">The patch operations.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<BulkResult> PatchWhereAsync<T>(
        Expression<Func<T, object?>> key,
        object value,
        Action<PatchBuilder<T>> patch,
        CancellationToken ct = default)
        where T : class
    {
        var keyMetadata = ResolveDeclaredKey(key);
        // Path interpolated as a validated literal, not bound — a bound parameter defeats
        // the declared-key expression index (ValidatedKeyJsonPath's remarks).
        return BulkPatchAsync(
            patch,
            $"AND json_extract(data, '{ValidatedKeyJsonPath(keyMetadata)}') = @keyValue",
            cmd => cmd.Parameters.AddWithValue("keyValue", ToKeyComparisonValue(value)),
            ct);
    }

    /// <summary>
    /// Deletes an explicit list of documents — one delete change record per document (ADR-014).
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="ids">The document identifiers.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<BulkResult> DeleteManyAsync<T>(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            InputValidator.ValidateDocumentId(id);
        }

        return BulkDeleteAsync<T>(
            "AND id IN (SELECT value FROM json_each(@ids))",
            cmd => cmd.Parameters.AddWithValue("ids", JsonSerializer.Serialize(ids)),
            ct);
    }

    /// <summary>
    /// Deletes all documents matching a declared key (ADR-006/014).
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="key">The declared key property.</param>
    /// <param name="value">The key value to match.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<BulkResult> DeleteWhereAsync<T>(
        Expression<Func<T, object?>> key,
        object value,
        CancellationToken ct = default)
        where T : class
    {
        var keyMetadata = ResolveDeclaredKey(key);
        return BulkDeleteAsync<T>(
            $"AND json_extract(data, '{ValidatedKeyJsonPath(keyMetadata)}') = @keyValue",
            cmd => cmd.Parameters.AddWithValue("keyValue", ToKeyComparisonValue(value)),
            ct);
    }

    private async Task<BulkResult> BulkPatchAsync<T>(
        Action<PatchBuilder<T>> patch,
        string predicate,
        Action<SqliteCommand> addPredicateParameters,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(patch);
        var metadata = _model.GetRequired<T>();
        var operations = CollectOperations(patch);

        return await ExecuteWriteAsync(async (conn, tx) =>
        {
            var rows = new List<(string Id, string OldJson, int SchemaVersion)>();
            await using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.Transaction = tx;
                selectCmd.CommandText = $"""
                    SELECT id, data, schema_version FROM document
                    WHERE scope = @scope AND tenant_id = @tenantId AND document_type = @type
                      {predicate}
                    """;
                AddScopeParameters(selectCmd, metadata.Name);
                addPredicateParameters(selectCmd);

                await using var reader = await selectCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
                }
            }

            var changes = new List<(string Id, long Version, int SchemaVersion, DocumentDiff Diff)>(rows.Count);
            var now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var (id, oldJsonText, oldSchemaVersion) in rows)
            {
                // Any violation rolls the entire savepoint back — bulk is atomic (ADR-014).
                EnsurePatchableSchema(metadata, id, oldSchemaVersion);

                var patchedJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
                JsonPatchApplier.Apply(patchedJson, operations);
                RunValidator(metadata, patchedJson);

                await using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = """
                    UPDATE document SET data = @data, version = version + 1, updated_by = @actorId, updated_at = @now
                    WHERE scope = @scope AND tenant_id = @tenantId AND document_type = @type AND id = @id
                    RETURNING version
                    """;
                AddIdentityParameters(updateCmd, metadata.Name, id);
                updateCmd.Parameters.AddWithValue("actorId", ActorId);
                updateCmd.Parameters.AddWithValue("data", patchedJson.ToJsonString());
                updateCmd.Parameters.AddWithValue("now", now);

                var newVersion = (long)(await ExecuteMappingKeyViolationsAsync(
                    () => updateCmd.ExecuteScalarAsync(ct), metadata.Name))!;

                var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
                var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, patchedJson), metadata, id);
                changes.Add((id, newVersion, metadata.SchemaVersion, diff));
            }

            await InsertChangeRecordsAsync(conn, tx, metadata, changes, ChangeOperation.Update, ct);
            return new BulkResult(rows.Count, CorrelationId);
        }, ct);
    }

    private async Task<BulkResult> BulkDeleteAsync<T>(
        string predicate,
        Action<SqliteCommand> addPredicateParameters,
        CancellationToken ct)
        where T : class
    {
        var metadata = _model.GetRequired<T>();

        return await ExecuteWriteAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                DELETE FROM document
                WHERE scope = @scope AND tenant_id = @tenantId AND document_type = @type
                  {predicate}
                RETURNING id, data, version, schema_version
                """;
            AddScopeParameters(cmd, metadata.Name);
            addPredicateParameters(cmd);

            var rows = new List<(string Id, string OldJson, long Version, int SchemaVersion)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3)));
                }
            }

            var changes = new List<(string Id, long Version, int SchemaVersion, DocumentDiff Diff)>(rows.Count);
            foreach (var (id, oldJsonText, version, oldSchemaVersion) in rows)
            {
                EnsureSchemaNotNewer(metadata, id, oldSchemaVersion);

                var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
                var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, after: null), metadata, id);
                changes.Add((id, version + 1, oldSchemaVersion, diff));
            }

            await InsertChangeRecordsAsync(conn, tx, metadata, changes, ChangeOperation.Delete, ct);
            return new BulkResult(rows.Count, CorrelationId);
        }, ct);
    }

    private static List<PatchOperation> CollectOperations<T>(Action<PatchBuilder<T>> patch)
        where T : class
    {
        var builder = new PatchBuilder<T>();
        patch(builder);
        if (builder.Operations.Count == 0)
        {
            throw new ArgumentException("Patch must contain at least one operation.", nameof(patch));
        }

        return builder.Operations;
    }

    private static void EnsurePatchableSchema(DocumentTypeMetadata metadata, string id, int storedSchemaVersion)
    {
        EnsureSchemaNotNewer(metadata, id, storedSchemaVersion);
        if (storedSchemaVersion < metadata.SchemaVersion)
        {
            // Throwing rolls the whole savepoint back — no read on this path to
            // materialize/upcast from (ADR-012).
            throw new SchemaUpcastRequiredException(metadata.Name, id, storedSchemaVersion, metadata.SchemaVersion);
        }
    }

    private static void RunValidator(DocumentTypeMetadata metadata, JsonObject storedNewJson)
    {
        if (metadata.Validator is null)
        {
            return;
        }

        var document = storedNewJson.Deserialize(metadata.ClrType, KernelJson.Options)
            ?? throw new InvalidOperationException($"Document of type {metadata.Name} deserialized to null.");
        metadata.Validator(document); // throws to reject — the savepoint rolls back
    }
}
