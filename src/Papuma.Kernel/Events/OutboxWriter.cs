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
    private readonly OutboxWriterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxWriter"/> class.
    /// </summary>
    /// <param name="options">Optional writer configuration.</param>
    public OutboxWriter(OutboxWriterOptions? options = null)
    {
        _options = options ?? new OutboxWriterOptions();
    }

    /// <summary>
    /// Enqueues a scoped outbox message in the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="scope">The scope context for the outbox message.</param>
    /// <param name="eventId">The event identifier associated with the outbox entry.</param>
    /// <param name="eventType">The business event type.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task EnqueueAsync(
        NpgsqlTransaction transaction,
        ScopeContext scope,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default)
    {
        InputValidator.ValidateEventType(eventType);
        InputValidator.ValidatePayloadSize(payloadJson, _options.MaxPayloadSizeBytes);

        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(scope);

        await transaction.Connection!.SetScopeAsync(scope, ct);

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO event_outbox (scope, tenant_id, event_id, event_type, payload)
            VALUES (@scope, @tenantId, @eventId, @eventType, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", (object?)scope.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }

}
