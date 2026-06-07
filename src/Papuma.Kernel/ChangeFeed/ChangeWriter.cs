// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.ChangeFeed;

/// <summary>
/// Writes change and business event records into the unified PostgreSQL-backed event feed.
/// </summary>
public sealed class ChangeWriter
{
    private const string IdempotencyConflictConstraintName = "ux_papuma_event_feed_idempotency_key";

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
    /// Appends a change record (state mutation) to the current transaction.
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
    /// <param name="idempotencyKey">Optional idempotency key for deduplication.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task AppendChangeAsync(
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
        ValidateChangeInputs(entity, entityId, eventType, version, payloadJson, actorId, idempotencyKey);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(scope);

        await AppendAsync(
            transaction, scope,
            kind: "Change",
            eventId: null,
            entity: entity,
            entityId: entityId,
            eventType: eventType,
            version: version,
            payloadJson: payloadJson,
            actorId: actorId,
            correlationId: correlationId,
            causationId: causationId,
            idempotencyKey: idempotencyKey,
            ct: ct);
    }

    /// <summary>
    /// Appends a business event record (semantic event without required state mutation) to the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="scope">The scope context for the event.</param>
    /// <param name="eventType">The business event type.</param>
    /// <param name="actorId">The actor that caused the event.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="entity">The optional logical entity name.</param>
    /// <param name="entityId">The optional logical entity identifier.</param>
    /// <param name="correlationId">The optional correlation identifier for distributed tracing.</param>
    /// <param name="causationId">The optional causation identifier pointing to the upstream event.</param>
    /// <param name="idempotencyKey">Optional idempotency key for deduplication.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The generated event identifier.</returns>
    public async Task<Guid> AppendEventAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        string eventType,
        string actorId,
        string payloadJson,
        string? entity = null,
        string? entityId = null,
        string? correlationId = null,
        string? causationId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        InputValidator.ValidateEventType(eventType);
        InputValidator.ValidateActorId(actorId);
        InputValidator.ValidatePayloadSize(payloadJson, _options.MaxPayloadSizeBytes);

        if (idempotencyKey is not null)
        {
            InputValidator.ValidateIdempotencyKey(idempotencyKey);
        }

        if (entity is not null)
        {
            InputValidator.ValidateEntity(entity);
        }

        if (entityId is not null)
        {
            InputValidator.ValidateEntityId(entityId);
        }

        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(scope);

        var eventId = Guid.NewGuid();

        try
        {
            await AppendAsync(
                transaction, scope,
                kind: "Event",
                eventId: eventId,
                entity: entity,
                entityId: entityId,
                eventType: eventType,
                version: null,
                payloadJson: payloadJson,
                actorId: actorId,
                correlationId: correlationId,
                causationId: causationId,
                idempotencyKey: idempotencyKey,
                ct: ct);
        }
        catch (PostgresException ex) when (
            idempotencyKey is not null &&
            ex.SqlState == PostgresErrorCodes.UniqueViolation &&
            string.Equals(ex.ConstraintName, IdempotencyConflictConstraintName, StringComparison.Ordinal))
        {
            await transaction.Connection!.SetScopeAsync(scope, ct);

            await using var lookupCmd = transaction.Connection!.CreateCommand();
            lookupCmd.Transaction = transaction;
            lookupCmd.CommandText = """
                SELECT event_id
                FROM papuma_event_feed
                WHERE scope = @scope
                  AND ((@tenantId IS NULL AND tenant_id IS NULL) OR tenant_id = @tenantId)
                  AND idempotency_key = @idempotencyKey
                """;
            lookupCmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            lookupCmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
            lookupCmd.Parameters.AddWithValue("idempotencyKey", idempotencyKey);

            var existingEventId = await lookupCmd.ExecuteScalarAsync(ct);
            return existingEventId is Guid guid ? guid : eventId;
        }

        return eventId;
    }

    private async Task AppendAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        string kind,
        Guid? eventId,
        string? entity,
        string? entityId,
        string eventType,
        int? version,
        string payloadJson,
        string actorId,
        string? correlationId,
        string? causationId,
        string? idempotencyKey,
        CancellationToken ct)
    {
        await transaction.Connection!.SetScopeAsync(scope, ct);

        try
        {
            await using var cmd = transaction.Connection!.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO papuma_event_feed
                    (kind, event_id, scope, tenant_id, entity, entity_id, event_type, version,
                     correlation_id, causation_id, actor_id, payload, idempotency_key)
                VALUES
                    (@kind, @eventId, @scope, @tenantId, @entity, @entityId, @eventType, @version,
                     @correlationId, @causationId, @actorId, @payload::jsonb, @idempotencyKey)
                """;

            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("eventId", (object?)eventId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("entity", (object?)entity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("entityId", (object?)entityId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("eventType", eventType);
            cmd.Parameters.AddWithValue("version", (object?)version ?? DBNull.Value);
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
    /// Validates all mandatory inputs for a change record.
    /// </summary>
    private void ValidateChangeInputs(
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        string actorId,
        string? idempotencyKey)
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
