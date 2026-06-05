// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.ChangeFeed;

/// <summary>
/// Writes validated change feed records into the PostgreSQL-backed change feed store.
/// </summary>
public sealed class ChangeWriter
{
    private const string IdempotencyConflictConstraintName = "ux_change_feed_idempotency_key";

    private readonly ChangeWriterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeWriter"/> class.
    /// </summary>
    /// <param name="options">Optional writer configuration.</param>
    public ChangeWriter(ChangeWriterOptions? options = null)
    {
        _options = options ?? new ChangeWriterOptions();
    }

    /// <summary>
    /// Appends a new scoped change feed record to the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="scope">The scope context for the record.</param>
    /// <param name="entity">The logical entity name that produced the change.</param>
    /// <param name="entityId">The entity identifier within its logical namespace.</param>
    /// <param name="eventType">The event type that describes the change.</param>
    /// <param name="version">The aggregate version associated with the change.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="actorId">The actor that caused the change.</param>
    /// <param name="correlationId">Optional correlation identifier for distributed tracing.</param>
    /// <param name="causationId">Optional causation identifier pointing to the upstream event.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task AppendAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        string actorId,
        string? correlationId = null,
        string? causationId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        ValidateInputs(entity, entityId, eventType, version, payloadJson, actorId, idempotencyKey);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(scope);

        await transaction.Connection!.SetScopeAsync(scope, ct);

        try
        {
            await using var cmd = transaction.Connection!.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO change_feed
                    (scope, tenant_id, entity, entity_id, event_type, version, correlation_id, causation_id, actor_id, payload, idempotency_key)
                VALUES
                    (@scope, @tenantId, @entity, @entityId, @eventType, @version, @correlationId, @causationId, @actorId, @payload::jsonb, @idempotencyKey)
                """;

            cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("entity", entity);
            cmd.Parameters.AddWithValue("entityId", entityId);
            cmd.Parameters.AddWithValue("eventType", eventType);
            cmd.Parameters.AddWithValue("version", version);
            cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("causationId", (object?)causationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("actorId", actorId);
            cmd.Parameters.AddWithValue("payload", payloadJson);
            cmd.Parameters.AddWithValue("idempotencyKey", (object?)idempotencyKey ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (
            idempotencyKey is not null &&
            ex.SqlState == PostgresErrorCodes.UniqueViolation &&
            string.Equals(ex.ConstraintName, IdempotencyConflictConstraintName, StringComparison.Ordinal))
        {
            return;
        }
    }

    /// <summary>
    /// Validates change feed input values before they are written to the store.
    /// </summary>
    /// <param name="entity">The logical entity name that produced the change.</param>
    /// <param name="entityId">The entity identifier within its logical namespace.</param>
    /// <param name="eventType">The event type that describes the change.</param>
    /// <param name="version">The aggregate version associated with the change.</param>
    /// <param name="payloadJson">The JSON payload to validate.</param>
    /// <param name="actorId">The actor that caused the change.</param>
    public void ValidateInputs(
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        string actorId,
        string? idempotencyKey = null)
    {
        InputValidator.ValidateEntity(entity);
        InputValidator.ValidateEntityId(entityId);
        InputValidator.ValidateEventType(eventType);
        InputValidator.ValidateVersion(version);
        InputValidator.ValidateActorId(actorId);
        InputValidator.ValidatePayloadSize(payloadJson, _options.MaxPayloadSizeBytes);

        if (idempotencyKey is not null)
        {
            InputValidator.ValidateIdempotencyKey(idempotencyKey);
        }
    }
}
