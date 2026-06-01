// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Events;

/// <summary>
/// Enqueues external publication work into the transactional outbox.
/// </summary>
public sealed class OutboxWriter
{
    /// <summary>
    /// Enqueues an outbox message in the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="eventId">The event identifier associated with the outbox entry.</param>
    /// <param name="eventType">The business event type.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task EnqueueAsync(
        NpgsqlTransaction transaction,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default)
        => await EnqueueAsync(
            transaction,
            TenantContext.Default,
            eventId,
            eventType,
            payloadJson,
            ct);

    /// <summary>
    /// Enqueues a tenant-scoped outbox message in the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="tenant">The tenant context for the outbox message.</param>
    /// <param name="eventId">The event identifier associated with the outbox entry.</param>
    /// <param name="eventType">The business event type.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task EnqueueAsync(
        NpgsqlTransaction transaction,
        TenantContext tenant,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default)
    {
        InputValidator.ValidateEventType(eventType);
        InputValidator.ValidatePayloadSize(payloadJson, _options.MaxPayloadSizeBytes);

        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(tenant);

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO event_outbox (tenant_id, event_id, event_type, payload)
            VALUES (@tenantId, @eventId, @eventType, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("tenantId", tenant.TenantId);
        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}