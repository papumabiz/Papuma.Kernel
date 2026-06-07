// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// PostgreSQL-backed implementation for versioned sensitive data storage and resolution.
/// </summary>
public sealed class NpgsqlSensitiveDataStore : ISensitiveDataStore, ISensitiveDataResolver
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="NpgsqlSensitiveDataStore"/> class.
    /// </summary>
    /// <param name="dataSource">The data source used for all sensitive data operations.</param>
    public NpgsqlSensitiveDataStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<SensitiveDataVersion> AppendAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        int schemaVersion,
        string payloadJson,
        string actorId,
        string? reason = null,
        CancellationToken ct = default)
    {
        ValidateWriteInputs(scope, sensitiveRef, schemaVersion, payloadJson, actorId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.SetScopeAsync(scope, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var current = await LoadLatestRowAsync(conn, tx, scope, sensitiveRef, lockRow: false, ct);
        var nextVersion = (current?.Version ?? 0) + 1;

        await InsertVersionAsync(
            conn,
            tx,
            scope,
            sensitiveRef,
            nextVersion,
            schemaVersion,
            payloadJson,
            redacted: false,
            deleted: false,
            legalHold: current?.LegalHold ?? false,
            actorId,
            reason,
            ct);

        await tx.CommitAsync(ct);

        return new SensitiveDataVersion(
            sensitiveRef,
            nextVersion,
            schemaVersion,
            payloadJson,
            SensitiveDataState.Active,
            current?.LegalHold ?? false,
            actorId,
            DateTimeOffset.UtcNow,
            reason,
            scope);
    }

    /// <inheritdoc />
    public async Task<SensitiveDataVersion?> GetLatestAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        CancellationToken ct = default)
    {
        ValidateScopeAndReference(scope, sensitiveRef);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.SetScopeAsync(scope, ct);

        var current = await LoadLatestRowAsync(conn, transaction: null, scope, sensitiveRef, lockRow: false, ct);
        return current is null
            ? null
            : ToVersion(current, scope, sensitiveRef);
    }

    /// <inheritdoc />
    public async Task MarkRedactedAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ValidateStateTransitionInputs(scope, sensitiveRef, actorId, reason);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.SetScopeAsync(scope, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var latest = await RequireLatestRowForMutationAsync(conn, tx, scope, sensitiveRef, ct);
        var nextVersion = latest.Version + 1;

        await InsertVersionAsync(
            conn,
            tx,
            scope,
            sensitiveRef,
            nextVersion,
            latest.SchemaVersion,
            "{\"redacted\":true}",
            redacted: true,
            deleted: latest.Deleted,
            latest.LegalHold,
            actorId,
            reason,
            ct);

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task MarkDeletedAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ValidateStateTransitionInputs(scope, sensitiveRef, actorId, reason);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.SetScopeAsync(scope, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var latest = await RequireLatestRowForMutationAsync(conn, tx, scope, sensitiveRef, ct);
        var nextVersion = latest.Version + 1;

        await InsertVersionAsync(
            conn,
            tx,
            scope,
            sensitiveRef,
            nextVersion,
            latest.SchemaVersion,
            "{\"deleted\":true}",
            redacted: true,
            deleted: true,
            latest.LegalHold,
            actorId,
            reason,
            ct);

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetLegalHoldAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        bool enabled,
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ValidateStateTransitionInputs(scope, sensitiveRef, actorId, reason);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.SetScopeAsync(scope, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var latest = await RequireLatestRowForMutationAsync(conn, tx, scope, sensitiveRef, ct);
        var nextVersion = latest.Version + 1;

        await InsertVersionAsync(
            conn,
            tx,
            scope,
            sensitiveRef,
            nextVersion,
            latest.SchemaVersion,
            latest.PayloadJson,
            latest.Redacted,
            latest.Deleted,
            enabled,
            actorId,
            reason,
            ct);

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string?> TryResolveLatestPayloadAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(scope, sensitiveRef, ct);
        if (latest is null)
        {
            return null;
        }

        return latest.State == SensitiveDataState.Active ? latest.PayloadJson : null;
    }

    private static void ValidateWriteInputs(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        int schemaVersion,
        string payloadJson,
        string actorId)
    {
        ValidateScopeAndReference(scope, sensitiveRef);
        InputValidator.ValidateVersion(schemaVersion);
        ArgumentNullException.ThrowIfNull(payloadJson);
        InputValidator.ValidateActorId(actorId);
    }

    private static void ValidateStateTransitionInputs(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        string actorId,
        string reason)
    {
        ValidateScopeAndReference(scope, sensitiveRef);
        InputValidator.ValidateActorId(actorId);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason is required for sensitive data state transitions.", nameof(reason));
        }
    }

    private static void ValidateScopeAndReference(ScopeContext scope, SensitiveRef sensitiveRef)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (sensitiveRef.IsEmpty)
        {
            throw new ArgumentException("sensitiveRef must not be empty.", nameof(sensitiveRef));
        }
    }

    private static async Task InsertVersionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        int version,
        int schemaVersion,
        string payloadJson,
        bool redacted,
        bool deleted,
        bool legalHold,
        string actorId,
        string? reason,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO papuma_sensitive_data_versions
                (sensitive_ref, version, scope, tenant_id, schema_version, payload,
                 redacted, deleted, legal_hold, reason, actor_id)
            VALUES
                (@sensitiveRef, @version, @scope, @tenantId, @schemaVersion, @payload::jsonb,
                 @redacted, @deleted, @legalHold, @reason, @actorId)
            """;

        cmd.Parameters.AddWithValue("sensitiveRef", sensitiveRef.Value);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("schemaVersion", schemaVersion);
        cmd.Parameters.AddWithValue("payload", payloadJson);
        cmd.Parameters.AddWithValue("redacted", redacted);
        cmd.Parameters.AddWithValue("deleted", deleted);
        cmd.Parameters.AddWithValue("legalHold", legalHold);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actorId", actorId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<LatestRow?> LoadLatestRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        bool lockRow,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = lockRow
            ? """
                SELECT version, schema_version, payload::text, redacted, deleted, legal_hold, actor_id, created_at, reason
                FROM papuma_sensitive_data_versions
                WHERE sensitive_ref = @sensitiveRef
                  AND scope = @scope
                  AND ((@tenantId IS NULL AND tenant_id IS NULL) OR tenant_id = @tenantId)
                ORDER BY version DESC
                LIMIT 1
                FOR UPDATE
                """
            : """
                SELECT version, schema_version, payload::text, redacted, deleted, legal_hold, actor_id, created_at, reason
                FROM papuma_sensitive_data_versions
                WHERE sensitive_ref = @sensitiveRef
                  AND scope = @scope
                  AND ((@tenantId IS NULL AND tenant_id IS NULL) OR tenant_id = @tenantId)
                ORDER BY version DESC
                LIMIT 1
                """;

        cmd.Parameters.AddWithValue("sensitiveRef", sensitiveRef.Value);
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new LatestRow(
            Version: reader.GetInt32(0),
            SchemaVersion: reader.GetInt32(1),
            PayloadJson: reader.GetString(2),
            Redacted: reader.GetBoolean(3),
            Deleted: reader.GetBoolean(4),
            LegalHold: reader.GetBoolean(5),
            ActorId: reader.GetString(6),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
            Reason: reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static async Task<LatestRow> RequireLatestRowForMutationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        CancellationToken ct)
    {
        var row = await LoadLatestRowAsync(conn, tx, scope, sensitiveRef, lockRow: true, ct);
        return row ?? throw new InvalidOperationException(
            $"No sensitive data exists for reference '{sensitiveRef.Value}' in scope '{scope.Scope}'.");
    }

    private static SensitiveDataVersion ToVersion(LatestRow row, ScopeContext scope, SensitiveRef sensitiveRef)
    {
        var state = row.Deleted
            ? SensitiveDataState.Deleted
            : row.Redacted
                ? SensitiveDataState.Redacted
                : SensitiveDataState.Active;

        return new SensitiveDataVersion(
            sensitiveRef,
            row.Version,
            row.SchemaVersion,
            row.PayloadJson,
            state,
            row.LegalHold,
            row.ActorId,
            row.CreatedAt,
            row.Reason,
            scope);
    }

    private sealed record LatestRow(
        int Version,
        int SchemaVersion,
        string PayloadJson,
        bool Redacted,
        bool Deleted,
        bool LegalHold,
        string ActorId,
        DateTimeOffset CreatedAt,
        string? Reason);
}