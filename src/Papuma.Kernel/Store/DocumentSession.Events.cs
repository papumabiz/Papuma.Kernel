// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Events;

namespace Papuma.Kernel.Store;

/// <summary>
/// Appending facts to the event log (ADR-013).
/// </summary>
public sealed partial class DocumentSession
{
    /// <summary>
    /// Appends a fact to the event log — for occurrences without state truth
    /// (<c>UserLoggedIn</c>, <c>EmailSent</c>). State transitions belong in documents,
    /// triggers in handlers (ADR-013 decision rule). The append commits atomically
    /// with the session's other writes and shares their <see cref="CorrelationId"/>.
    /// Payload fields pass policy application (ADR-007) before storage.
    /// </summary>
    /// <typeparam name="TEvent">The registered event CLR type.</typeparam>
    /// <param name="event">The event to append.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The event log sequence number.</returns>
    public async Task<long> AppendAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        var metadata = _model.GetRequiredEvent<TEvent>();

        var payload = JsonSerializer.SerializeToNode(@event, KernelJson.Options) as JsonObject
            ?? throw new ArgumentException(
                $"Event of type {typeof(TEvent).Name} must serialize to a JSON object.", nameof(@event));
        payload = EventPayloadPolicyApplier.Apply(payload, metadata);

        return await ExecuteWriteAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO papuma.event (scope, tenant_id, event_type, payload, metadata)
                VALUES (@scope, @tenantId, @type, @payload, @metadata)
                RETURNING seq
                """;
            cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
            cmd.Parameters.AddWithValue("type", metadata.Name);
            AddJsonbParameter(cmd, "payload", payload);
            AddJsonbParameter(cmd, "metadata", BuildChangeMetadata(null));

            var seq = (long)(await cmd.ExecuteScalarAsync(ct))!;
            _hasWrites = true;
            return seq;
        }, ct);
    }
}
