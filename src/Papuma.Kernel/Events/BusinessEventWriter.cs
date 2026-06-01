// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Events;

/// <summary>
/// Writes business events into the current PostgreSQL transaction.
/// </summary>
public sealed class BusinessEventWriter
{
    private readonly BusinessEventWriterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="BusinessEventWriter"/> class.
    /// </summary>
    /// <param name="options">Optional writer configuration.</param>
    public BusinessEventWriter(BusinessEventWriterOptions? options = null)
    {
        _options = options ?? new BusinessEventWriterOptions();
    }

    /// <summary>
    /// Appends a business event to the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="eventType">The business event type.</param>
    /// <param name="actorId">The actor that caused the event.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="entity">The optional logical entity name.</param>
    /// <param name="entityId">The optional logical entity identifier.</param>
    /// <param name="correlationId">The optional correlation identifier for distributed tracing.</param>
    /// <param name="causationId">The optional causation identifier pointing to the upstream event.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The generated event identifier.</returns>
    public async Task<Guid> AppendAsync(
        NpgsqlTransaction transaction,
        string eventType,
        string actorId,
        string payloadJson,
        string? entity = null,
        string? entityId = null,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken ct = default)
        => await AppendAsync(
            transaction,
            TenantContext.Default,
            eventType,
            actorId,
            payloadJson,
            entity,
            entityId,
            correlationId,
            causationId,
            ct);

    /// <summary>
    /// Appends a tenant-scoped business event to the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="tenant">The tenant context for the event.</param>
    /// <param name="eventType">The business event type.</param>
    /// <param name="actorId">The actor that caused the event.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="entity">The optional logical entity name.</param>
    /// <param name="entityId">The optional logical entity identifier.</param>
    /// <param name="correlationId">The optional correlation identifier for distributed tracing.</param>
    /// <param name="causationId">The optional causation identifier pointing to the upstream event.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The generated event identifier.</returns>
    public async Task<Guid> AppendAsync(
        NpgsqlTransaction transaction,
        TenantContext tenant,
        string eventType,
        string actorId,
        string payloadJson,
        string? entity = null,
        string? entityId = null,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken ct = default)
    {
        InputValidator.ValidateEventType(eventType);
        InputValidator.ValidateActorId(actorId);
        InputValidator.ValidatePayloadSize(payloadJson, _options.MaxPayloadSizeBytes);

        if (entity is not null)
        {
            InputValidator.ValidateEntity(entity);
        }

        if (entityId is not null)
        {
            InputValidator.ValidateEntityId(entityId);
        }

        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(tenant);

        var eventId = Guid.NewGuid();

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO business_event_log
                (event_id, tenant_id, event_type, entity, entity_id, actor_id,
                 correlation_id, causation_id, payload)
            VALUES
                (@eventId, @tenantId, @eventType, @entity, @entityId, @actorId,
                 @correlationId, @causationId, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("tenantId", tenant.TenantId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("entity", (object?)entity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("entityId", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actorId", actorId);
        cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("causationId", (object?)causationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);

        return eventId;
    }
}
