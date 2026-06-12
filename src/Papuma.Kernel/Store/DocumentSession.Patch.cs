// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Partial updates (ADR-012) and set-based bulk operations (ADR-014).
/// </summary>
public sealed partial class DocumentSession
{
    /// <summary>
    /// Applies a partial update without loading the document — the read happens inside
    /// the UPDATE statement itself (<c>jsonb_set</c> + <c>RETURNING OLD/NEW</c>, ADR-012).
    /// Diff, policies and change record work exactly like a full save.
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
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var dataExpression = BuildPatchExpression(cmd, operations);
            var versionPredicate = expectedVersion is null ? string.Empty : "AND version = @expectedVersion";
            cmd.CommandText = $"""
                UPDATE papuma.document
                SET data = {dataExpression},
                    version = version + 1,
                    updated_at = now()
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND document_type = @type AND id = @id
                  {versionPredicate}
                RETURNING old.data::text, new.data::text, new.version, old.schema_version
                """;
            AddIdentityParameters(cmd, metadata.Name, id);
            if (expectedVersion is not null)
            {
                cmd.Parameters.AddWithValue("expectedVersion", expectedVersion.Value);
            }

            var row = await ExecuteMappingKeyViolationsAsync(
                async () =>
                {
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                    {
                        return ((string, string, long, int)?)null;
                    }

                    return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3));
                },
                metadata.Name);

            if (row is null)
            {
                throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion ?? 0, ct);
            }

            var (oldJsonText, newJsonText, newVersion, oldSchemaVersion) = row.Value;
            EnsurePatchableSchema(metadata, id, oldSchemaVersion);

            var storedNewJson = (JsonObject)JsonNode.Parse(newJsonText)!;
            RunValidator(metadata, storedNewJson);

            var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
            var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, storedNewJson), metadata, id);

            await InsertChangeRecordAsync(
                conn, tx, metadata, id, newVersion, ChangeOperation.Update, diff, metadata.SchemaVersion, ct);

            return new SaveResult(newVersion, ChangeOperation.Update, diff);
        }, ct);
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
    }

    /// <summary>
    /// Applies the same patch to an explicit list of documents — one atomic statement,
    /// one change record per affected document, all carrying the session's correlation
    /// id (ADR-014). No <c>expectedVersion</c>: bulk operates on current state by definition.
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
            "AND id = ANY(@ids)",
            cmd => cmd.Parameters.AddWithValue("ids", ids.ToArray()),
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
        return BulkPatchAsync(
            patch,
            "AND data #>> @keyPath = @keyValue",
            cmd =>
            {
                cmd.Parameters.AddWithValue("keyPath", keyMetadata.PathSegments);
                cmd.Parameters.AddWithValue("keyValue", ToKeyText(value));
            },
            ct);
    }

    /// <summary>
    /// Deletes an explicit list of documents — one atomic statement, one delete change
    /// record per document (ADR-014).
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
            "AND id = ANY(@ids)",
            cmd => cmd.Parameters.AddWithValue("ids", ids.ToArray()),
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
            "AND data #>> @keyPath = @keyValue",
            cmd =>
            {
                cmd.Parameters.AddWithValue("keyPath", keyMetadata.PathSegments);
                cmd.Parameters.AddWithValue("keyValue", ToKeyText(value));
            },
            ct);
    }

    private async Task<BulkResult> BulkPatchAsync<T>(
        Action<PatchBuilder<T>> patch,
        string predicate,
        Action<NpgsqlCommand> addPredicateParameters,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(patch);
        var metadata = _model.GetRequired<T>();
        var operations = CollectOperations(patch);

        return await ExecuteWriteAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var dataExpression = BuildPatchExpression(cmd, operations);
            cmd.CommandText = $"""
                UPDATE papuma.document
                SET data = {dataExpression},
                    version = version + 1,
                    updated_at = now()
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND document_type = @type
                  {predicate}
                RETURNING id, old.data::text, new.data::text, new.version, old.schema_version
                """;
            AddScopeParameters(cmd, metadata.Name);
            addPredicateParameters(cmd);

            var rows = await ExecuteMappingKeyViolationsAsync(
                async () =>
                {
                    var collected = new List<(string Id, string OldJson, string NewJson, long Version, int SchemaVersion)>();
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        collected.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                            reader.GetInt64(3), reader.GetInt32(4)));
                    }

                    return collected;
                },
                metadata.Name);

            foreach (var (id, oldJsonText, newJsonText, newVersion, oldSchemaVersion) in rows)
            {
                // Any violation rolls the entire statement back — bulk is atomic (ADR-014).
                EnsurePatchableSchema(metadata, id, oldSchemaVersion);

                var storedNewJson = (JsonObject)JsonNode.Parse(newJsonText)!;
                RunValidator(metadata, storedNewJson);

                var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
                var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, storedNewJson), metadata, id);
                await InsertChangeRecordAsync(
                    conn, tx, metadata, id, newVersion, ChangeOperation.Update, diff,
                    metadata.SchemaVersion, ct);
            }

            return new BulkResult(rows.Count, CorrelationId);
        }, ct);
    }

    private async Task<BulkResult> BulkDeleteAsync<T>(
        string predicate,
        Action<NpgsqlCommand> addPredicateParameters,
        CancellationToken ct)
        where T : class
    {
        var metadata = _model.GetRequired<T>();

        return await ExecuteWriteAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                DELETE FROM papuma.document
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND document_type = @type
                  {predicate}
                RETURNING id, old.data::text, old.version, old.schema_version
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

            foreach (var (id, oldJsonText, version, oldSchemaVersion) in rows)
            {
                EnsureSchemaNotNewer(metadata, id, oldSchemaVersion);

                var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
                var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, after: null), metadata, id);
                await InsertChangeRecordAsync(
                    conn, tx, metadata, id, version + 1, ChangeOperation.Delete, diff,
                    oldSchemaVersion, ct);
            }

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

    /// <summary>
    /// Builds the nested <c>jsonb_set</c>/<c>#-</c> expression for the patch operations
    /// and registers their values as parameters. Paths and values are always bound as
    /// parameters — no patch content reaches the SQL text.
    /// </summary>
    private static string BuildPatchExpression(NpgsqlCommand cmd, IReadOnlyList<PatchOperation> operations)
    {
        var expression = new StringBuilder("data");
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var pathParam = $"patchPath{i}";
            cmd.Parameters.AddWithValue(pathParam, op.PathSegments);

            switch (op.Kind)
            {
                case PatchOperationKind.Set:
                    var valueParam = $"patchValue{i}";
                    cmd.Parameters.Add(new NpgsqlParameter(valueParam, NpgsqlDbType.Jsonb)
                    {
                        Value = op.Value?.ToJsonString() ?? "null",
                    });
                    expression.Insert(0, "jsonb_set(").Append($", @{pathParam}, @{valueParam}, true)");
                    break;

                case PatchOperationKind.Remove:
                    expression.Insert(0, '(').Append($" #- @{pathParam})");
                    break;

                case PatchOperationKind.Increment:
                    // Reads from the original `data` column: increments are relative to the
                    // stored state, atomic within the statement (ADR-012).
                    var byParam = $"patchValue{i}";
                    cmd.Parameters.AddWithValue(byParam, op.IncrementBy);
                    expression.Insert(0, "jsonb_set(").Append(
                        $", @{pathParam}, to_jsonb(COALESCE((data #>> @{pathParam})::numeric, 0) + @{byParam}), true)");
                    break;
            }
        }

        return expression.ToString();
    }

    private KeyMetadata ResolveDeclaredKey<T>(Expression<Func<T, object?>> key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        var metadata = _model.GetRequired<T>();
        var path = JsonPathResolver.Resolve(key);

        return metadata.Keys.FirstOrDefault(k => k.Path == path)
            ?? throw new ArgumentException(
                $"'{path}' is not a declared key on {metadata.Name}. " +
                "Declare it via [UniqueKey]/[LookupKey] or UniqueKey()/LookupKey() (ADR-006).",
                nameof(key));
    }

    private static void EnsurePatchableSchema(DocumentTypeMetadata metadata, string id, int storedSchemaVersion)
    {
        EnsureSchemaNotNewer(metadata, id, storedSchemaVersion);
        if (storedSchemaVersion < metadata.SchemaVersion)
        {
            // Throwing rolls the already-applied UPDATE back (ADR-012).
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
