// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Diagnostics;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

/// <summary>
/// Scope-bound unit of work for loading and writing documents (ADR-002/003, architecture §5).
/// </summary>
/// <remarks>
/// <para>
/// Every write is a single atomic statement using PostgreSQL 18 <c>RETURNING OLD/NEW</c>:
/// the previous state is captured in the same statement that changes the row — no prior
/// read, no race window. Every diff passes policy application (ADR-007) before reaching
/// the feed.
/// </para>
/// <para>
/// <b>Session = Unit of Work:</b> all operations share one transaction, opened lazily on
/// first use. Nothing is visible to other sessions until <see cref="CommitAsync"/>;
/// disposing without commit rolls everything back. Each write runs under a savepoint, so
/// a failed write (concurrency conflict, unique violation, rejected validator) leaves
/// the session usable and earlier writes intact. All change records of a session share
/// the <see cref="CorrelationId"/>.
/// </para>
/// </remarks>
public sealed partial class DocumentSession : IAsyncDisposable
{
    private const string WriteSavepoint = "papuma_write";

    private readonly NpgsqlDataSource _dataSource;
    private readonly KernelModel _model;
    private readonly SessionOptions _options;

    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private bool _disposed;
    private bool _hasWrites;

    /// <summary>Gets the scope this session is bound to.</summary>
    public ScopeContext Scope { get; }

    /// <summary>Gets the correlation id carried by all change records of this session.</summary>
    public Guid CorrelationId { get; }

    internal DocumentSession(NpgsqlDataSource dataSource, KernelModel model, ScopeContext scope, SessionOptions? options)
    {
        _dataSource = dataSource;
        _model = model;
        Scope = scope;
        _options = options ?? new SessionOptions();
        CorrelationId = _options.CorrelationId ?? Guid.NewGuid();
    }

    /// <summary>
    /// Commits all writes performed since the session was opened (or since the last
    /// commit). The next operation starts a fresh transaction.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is null)
        {
            return; // nothing pending
        }

        var stopwatch = Stopwatch.StartNew();

        if (_hasWrites)
        {
            // Wakeup for feed processors; delivered atomically with the commit (ADR-010).
            await using var notifyCmd = _connection!.CreateCommand();
            notifyCmd.Transaction = _transaction;
            notifyCmd.CommandText = $"NOTIFY {Processing.ChangeFeedProcessor.NotifyChannel}";
            await notifyCmd.ExecuteNonQueryAsync(ct);
        }

        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
        _hasWrites = false;

        KernelDiagnostics.SessionCommits.Add(1);
        KernelDiagnostics.CommitDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Disposes the session. Uncommitted writes are rolled back.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync(); // implicit rollback
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    /// <summary>
    /// Loads a document by id, or returns <c>null</c> when it does not exist in this
    /// scope. Sees the session's own uncommitted writes.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<DocumentResult<T>?> LoadAsync<T>(string id, CancellationToken ct = default)
        where T : class
    {
        var metadata = _model.GetRequired<T>();
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

        return await ReadSingleAsync<T>(cmd, metadata, id, ct);
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
        ArgumentNullException.ThrowIfNull(value);

        var metadata = _model.GetRequired<T>();
        var keyMetadata = ResolveDeclaredKey(key);

        var (conn, tx) = await EnsureTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, data::text, version, schema_version
            FROM papuma.document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type
              AND data #>> @path = @value
            LIMIT 2
            """;
        AddScopeParameters(cmd, metadata.Name);
        cmd.Parameters.AddWithValue("path", keyMetadata.PathSegments);
        cmd.Parameters.AddWithValue("value", ToKeyText(value));

        var results = new List<DocumentResult<T>>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var document = DeserializeDocument<T>(
                    metadata, reader.GetString(0), reader.GetString(1), reader.GetInt32(3));
                results.Add(new DocumentResult<T>(document, reader.GetInt64(2)));
            }
        }

        return results.Count switch
        {
            0 => null,
            1 => results[0],
            _ => throw new InvalidOperationException(
                $"Key {metadata.Name}.{keyMetadata.Path} matched multiple documents. " +
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

        using var activity = StartWriteActivity("save", metadata.Name, id);
        try
        {
            var result = await ExecuteWriteAsync(
                (conn, tx) => expectedVersion == 0
                    ? InsertAsync(conn, tx, metadata, id, newJson, ct)
                    : UpdateAsync(conn, tx, metadata, id, newJson, expectedVersion, ct, extraMetadata: null),
                ct);
            activity?.SetTag("papuma.version", result.Version);
            return result;
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
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

        using var activity = StartWriteActivity("delete", metadata.Name, id);
        try
        {
            return await ExecuteWriteAsync(async (conn, tx) =>
            {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM papuma.document
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND document_type = @type AND id = @id
                  AND version = @expectedVersion
                RETURNING old.data::text, old.schema_version
                """;
            AddIdentityParameters(cmd, metadata.Name, id);
            cmd.Parameters.AddWithValue("expectedVersion", expectedVersion);

            string? oldJsonText = null;
            var oldSchemaVersion = 0;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    oldJsonText = reader.GetString(0);
                    oldSchemaVersion = reader.GetInt32(1);
                }
            }

            if (oldJsonText is null)
            {
                throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion, ct);
            }

            EnsureSchemaNotNewer(metadata, id, oldSchemaVersion);

            var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
            var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, after: null), metadata, id);
            var deletedVersion = expectedVersion + 1;

            // The delete diff carries the old state — record its schema version, not the model's (ADR-005).
            await InsertChangeRecordAsync(
                conn, tx, metadata, id, deletedVersion, ChangeOperation.Delete, diff, oldSchemaVersion, ct);

            return new SaveResult(deletedVersion, ChangeOperation.Delete, diff);
            }, ct);
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
    }

    /// <summary>
    /// Rolls a document back to the state of <paramref name="toVersion"/> — recorded as
    /// a normal update with <c>isRollback</c>/<c>restoredVersion</c> metadata, never as
    /// a fourth operation; the history stays append-only (ADR-008).
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="toVersion">The version whose state is restored.</param>
    /// <param name="expectedVersion">The expected current version (optimistic concurrency).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="RollbackNotPossibleException">
    /// A diff on the way back contains policy entries without values (ADR-007).
    /// </exception>
    public async Task<SaveResult> RollbackAsync<T>(
        string id,
        long toVersion,
        long expectedVersion,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedVersion, toVersion);
        var metadata = _model.GetRequired<T>();
        InputValidator.ValidateDocumentId(id);

        using var activity = StartWriteActivity("rollback", metadata.Name, id);
        activity?.SetTag("papuma.restored_version", toVersion);
        try
        {
        return await ExecuteWriteAsync(async (conn, tx) =>
        {
            // 1. Current state (the concurrency guarantee comes from the final UPDATE's
            //    version predicate; this read only provides the reconstruction base).
            var current = await LoadRawAsync(conn, tx, metadata, id, ct)
                ?? throw new DocumentNotFoundException(metadata.Name, id);
            if (current.Version != expectedVersion)
            {
                throw new ConcurrencyException(metadata.Name, id, expectedVersion, current.Version);
            }

            EnsureSchemaNotNewer(metadata, id, current.SchemaVersion);

            // 2. Target record: must exist and must not be a delete.
            var target = await LoadChangeAsync(conn, tx, metadata, id, toVersion, ct)
                ?? throw new ArgumentException(
                    $"No change record at version {toVersion} for {metadata.Name}/{id}.", nameof(toVersion));
            if (target.Operation == ChangeOperation.Delete)
            {
                throw new ArgumentException(
                    $"Version {toVersion} of {metadata.Name}/{id} is a delete — " +
                    "a rollback cannot restore non-existence.", nameof(toVersion));
            }

            // 3. Reconstruct: apply the diffs back from current down to toVersion (ADR-004).
            var state = current.Data;
            foreach (var change in await LoadChangesDescendingAsync(conn, tx, metadata, id, toVersion, current.Version, ct))
            {
                var diff = DocumentDiff.FromJson(change);
                foreach (var (path, entry) in diff.Entries)
                {
                    if (entry.Kind != DiffEntryKind.Tracked)
                    {
                        throw new RollbackNotPossibleException(metadata.Name, id, path, entry.Kind);
                    }
                }

                state = JsonDiffEngine.ApplyReverse(state, diff);
            }

            // 4. Lift the reconstructed state through the upcaster chain (ADR-008) and validate.
            metadata.Upcast(state, target.SchemaVersion);
            RunValidator(metadata, state);

            // 5. Persist as a normal update with rollback metadata.
            var rollbackMetadata = new JsonObject
            {
                ["isRollback"] = true,
                ["restoredVersion"] = toVersion,
            };
            return await UpdateAsync(conn, tx, metadata, id, state, expectedVersion, ct, rollbackMetadata);
        }, ct);
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
    }

    // ── Write internals ────────────────────────────────────────────────────────

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
        await InsertChangeRecordAsync(
            conn, tx, metadata, id, insertedVersion, ChangeOperation.Insert, diff, metadata.SchemaVersion, ct);

        return new SaveResult(insertedVersion, ChangeOperation.Insert, diff);
    }

    private async Task<SaveResult> UpdateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DocumentTypeMetadata metadata,
        string id,
        JsonObject newJson,
        long expectedVersion,
        CancellationToken ct,
        JsonObject? extraMetadata)
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
            RETURNING old.data::text AS old_data, new.data::text AS new_data,
                      new.version AS new_version, old.schema_version AS old_schema_version
            """;
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("schemaVersion", metadata.SchemaVersion);
        cmd.Parameters.AddWithValue("expectedVersion", expectedVersion);
        AddJsonbParameter(cmd, "data", newJson);

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
            throw await VersionConflictAsync(conn, tx, metadata.Name, id, expectedVersion, ct);
        }

        var (oldJsonText, newJsonText, newVersion, oldSchemaVersion) = row.Value;

        // Throwing here rolls the already-applied UPDATE back — no silent back-migration (ADR-005).
        EnsureSchemaNotNewer(metadata, id, oldSchemaVersion);

        var oldJson = (JsonObject)JsonNode.Parse(oldJsonText)!;
        var storedNewJson = (JsonObject)JsonNode.Parse(newJsonText)!;
        var diff = PolicyApplier.Apply(JsonDiffEngine.Diff(oldJson, storedNewJson), metadata, id);

        await InsertChangeRecordAsync(
            conn, tx, metadata, id, newVersion, ChangeOperation.Update, diff,
            metadata.SchemaVersion, ct, extraMetadata);

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
        int schemaVersion,
        CancellationToken ct,
        JsonObject? extraMetadata = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO papuma.change
                (scope, tenant_id, document_type, document_id, version, schema_version, operation, diff, metadata)
            VALUES
                (@scope, @tenantId, @type, @id, @version, @schemaVersion, @operation, @diff, @metadata)
            """;
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("schemaVersion", schemaVersion);
        cmd.Parameters.AddWithValue("operation", (short)operation);
        AddJsonbParameter(cmd, "diff", diff.ToJson());
        AddJsonbParameter(cmd, "metadata", BuildChangeMetadata(extraMetadata));

        await cmd.ExecuteNonQueryAsync(ct);
        _hasWrites = true;

        KernelDiagnostics.Writes.Add(1,
            new KeyValuePair<string, object?>("papuma.operation", operation.ToString()),
            new KeyValuePair<string, object?>("papuma.document_type", metadata.Name));
    }

    /// <summary>
    /// Inserts the change records of one bulk operation as a single set-based
    /// statement (<c>unnest</c> instead of N inserts — the phase-5 note). All records
    /// share the operation and the session metadata (common correlation id, ADR-014).
    /// </summary>
    private async Task InsertChangeRecordsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DocumentTypeMetadata metadata,
        IReadOnlyList<(string Id, long Version, int SchemaVersion, DocumentDiff Diff)> records,
        ChangeOperation operation,
        CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        var ids = new string[records.Count];
        var versions = new long[records.Count];
        var schemaVersions = new int[records.Count];
        var diffs = new string[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            ids[i] = records[i].Id;
            versions[i] = records[i].Version;
            schemaVersions[i] = records[i].SchemaVersion;
            diffs[i] = records[i].Diff.ToJson().ToJsonString();
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO papuma.change
                (scope, tenant_id, document_type, document_id, version, schema_version, operation, diff, metadata)
            SELECT @scope, @tenantId, @type, u.id, u.version, u.schema_version, @operation, u.diff, @metadata
            FROM unnest(@ids, @versions, @schemaVersions, @diffs) AS u(id, version, schema_version, diff)
            """;
        AddScopeParameters(cmd, metadata.Name);
        cmd.Parameters.AddWithValue("operation", (short)operation);
        AddJsonbParameter(cmd, "metadata", BuildChangeMetadata(null));
        cmd.Parameters.AddWithValue("ids", ids);
        cmd.Parameters.AddWithValue("versions", versions);
        cmd.Parameters.Add(new NpgsqlParameter("schemaVersions", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = schemaVersions,
        });
        cmd.Parameters.Add(new NpgsqlParameter("diffs", NpgsqlDbType.Array | NpgsqlDbType.Jsonb)
        {
            Value = diffs,
        });

        await cmd.ExecuteNonQueryAsync(ct);
        _hasWrites = true;

        KernelDiagnostics.Writes.Add(records.Count,
            new KeyValuePair<string, object?>("papuma.operation", operation.ToString()),
            new KeyValuePair<string, object?>("papuma.document_type", metadata.Name));
    }

    /// <summary>
    /// Builds the change metadata for this session: correlation id always, actor and
    /// causation when configured, plus operation-specific extras (e.g. rollback markers).
    /// </summary>
    private JsonObject BuildChangeMetadata(JsonObject? extra)
    {
        var json = new JsonObject { ["correlationId"] = CorrelationId.ToString("N") };
        if (_options.ActorId is not null)
        {
            json["actorId"] = _options.ActorId;
        }

        if (_options.CausationId is not null)
        {
            json["causationId"] = _options.CausationId;
        }

        // Phase 11: propagate the active trace context through the feed so handler
        // spans can link back to the originating request.
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceparent })
        {
            json["traceparent"] = traceparent;
        }

        if (extra is not null)
        {
            foreach (var (key, node) in extra)
            {
                json[key] = node?.DeepClone();
            }
        }

        return json;
    }

    // ── Transaction plumbing ───────────────────────────────────────────────────

    /// <summary>
    /// Starts a write span (phase 11). Null when no listener is attached — near-zero cost.
    /// </summary>
    private Activity? StartWriteActivity(string operation, string documentType, string? documentId)
    {
        var activity = KernelDiagnostics.ActivitySource.StartActivity($"papuma.session.{operation}");
        if (activity is not null)
        {
            activity.SetTag("papuma.document_type", documentType);
            activity.SetTag("papuma.tenant", Scope.TenantId);
            if (documentId is not null)
            {
                activity.SetTag("papuma.document_id", documentId);
            }
        }

        return activity;
    }

    /// <summary>
    /// Exception-filter helper: marks the span as failed without catching the exception.
    /// </summary>
    private static bool RecordFailure(Activity? activity, Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        return false;
    }

    /// <summary>
    /// Opens the session transaction lazily (and re-applies the scope, since
    /// <c>SET LOCAL</c> is transaction-scoped).
    /// </summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> EnsureTransactionAsync(
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is null)
        {
            _connection ??= await _dataSource.OpenConnectionAsync(ct);
            _transaction = await _connection.BeginTransactionAsync(ct);
            await _connection.SetScopeAsync(Scope, ct);
        }

        return (_connection!, _transaction);
    }

    /// <summary>
    /// Runs a write under a savepoint: a failing write (typed conflict, unique violation,
    /// rejected validator) rolls back only itself — earlier session writes stay intact
    /// and the session remains usable.
    /// </summary>
    private async Task<TResult> ExecuteWriteAsync<TResult>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> write,
        CancellationToken ct)
    {
        var (conn, tx) = await EnsureTransactionAsync(ct);
        await tx.SaveAsync(WriteSavepoint, ct);
        try
        {
            var result = await write(conn, tx);
            await tx.ReleaseAsync(WriteSavepoint, ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(WriteSavepoint, ct);
            throw;
        }
    }

    // ── Rollback internals ─────────────────────────────────────────────────────

    private sealed record RawDocument(JsonObject Data, long Version, int SchemaVersion);

    private sealed record ChangeHead(ChangeOperation Operation, int SchemaVersion);

    private async Task<RawDocument?> LoadRawAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DocumentTypeMetadata metadata, string id, CancellationToken ct)
    {
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

        return new RawDocument(
            (JsonObject)JsonNode.Parse(reader.GetString(0))!,
            reader.GetInt64(1),
            reader.GetInt32(2));
    }

    private async Task<ChangeHead?> LoadChangeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DocumentTypeMetadata metadata, string id,
        long version, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT operation, schema_version
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND document_id = @id AND version = @version
            """;
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("version", version);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ChangeHead((ChangeOperation)reader.GetInt16(0), reader.GetInt32(1));
    }

    private async Task<IReadOnlyList<JsonObject>> LoadChangesDescendingAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DocumentTypeMetadata metadata, string id,
        long toVersionExclusive, long fromVersionInclusive, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT diff::text
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = @type AND document_id = @id
              AND version > @toVersion AND version <= @fromVersion
            ORDER BY version DESC
            """;
        AddIdentityParameters(cmd, metadata.Name, id);
        cmd.Parameters.AddWithValue("toVersion", toVersionExclusive);
        cmd.Parameters.AddWithValue("fromVersion", fromVersionInclusive);

        var diffs = new List<JsonObject>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            diffs.Add((JsonObject)JsonNode.Parse(reader.GetString(0))!);
        }

        return diffs;
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

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
            KernelDiagnostics.Conflicts.Add(1,
                new KeyValuePair<string, object?>("papuma.kind", "concurrency"),
                new KeyValuePair<string, object?>("papuma.document_type", documentType));
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
                KernelDiagnostics.Conflicts.Add(1,
                    new KeyValuePair<string, object?>("papuma.kind", "unique_key"),
                    new KeyValuePair<string, object?>("papuma.document_type", documentType));
                throw new UniqueKeyViolationException(documentType, match.Value.Key.Path, ex);
            }

            throw;
        }
    }

    private static async Task<DocumentResult<T>?> ReadSingleAsync<T>(
        NpgsqlCommand cmd, DocumentTypeMetadata metadata, string id, CancellationToken ct)
        where T : class
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        var version = reader.GetInt64(1);
        var schemaVersion = reader.GetInt32(2);
        var document = DeserializeDocument<T>(metadata, id, json, schemaVersion);

        return new DocumentResult<T>(document, version);
    }

    /// <summary>
    /// Deserializes a stored document, running the upcaster chain when the stored
    /// schema version is older than the model's (lazy upcasting, ADR-005). The stored
    /// row is not rewritten — persistence of the lifted state happens on the next save.
    /// </summary>
    private static T DeserializeDocument<T>(
        DocumentTypeMetadata metadata, string id, string json, int storedSchemaVersion)
        where T : class
    {
        EnsureSchemaNotNewer(metadata, id, storedSchemaVersion);

        T? document;
        if (storedSchemaVersion < metadata.SchemaVersion)
        {
            var raw = (JsonObject)JsonNode.Parse(json)!;
            metadata.Upcast(raw, storedSchemaVersion);
            document = raw.Deserialize<T>(KernelJson.Options);
        }
        else
        {
            document = JsonSerializer.Deserialize<T>(json, KernelJson.Options);
        }

        return document
            ?? throw new InvalidOperationException($"Document of type {metadata.Name} deserialized to null.");
    }

    private static void EnsureSchemaNotNewer(DocumentTypeMetadata metadata, string id, int storedSchemaVersion)
    {
        if (storedSchemaVersion > metadata.SchemaVersion)
        {
            throw new SchemaVersionConflictException(metadata.Name, id, storedSchemaVersion, metadata.SchemaVersion);
        }
    }

    private void AddIdentityParameters(NpgsqlCommand cmd, string documentType, string id)
    {
        AddScopeParameters(cmd, documentType);
        cmd.Parameters.AddWithValue("id", id);
    }

    private void AddScopeParameters(NpgsqlCommand cmd, string documentType)
    {
        cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("type", documentType);
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
