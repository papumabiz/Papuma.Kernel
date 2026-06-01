// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

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
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(payloadJson);

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO event_outbox (event_id, event_type, payload)
            VALUES (@eventId, @eventType, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}