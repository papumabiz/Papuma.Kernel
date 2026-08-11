// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;

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

        using var activity = StartWriteActivity("append", metadata.Name, documentId: null);
        try
        {
        return await ExecuteWriteAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO papuma.event (scope, tenant_id, event_type, payload, actor_id, metadata)
                VALUES (@scope, @tenantId, @type, @payload, @actorId, @metadata)
                RETURNING seq
                """;
            cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
            cmd.Parameters.AddWithValue("type", metadata.Name);
            AddJsonbParameter(cmd, "payload", payload);
            cmd.Parameters.AddWithValue("actorId", ActorId);
            AddJsonbParameter(cmd, "metadata", BuildChangeMetadata(null));

            var seq = (long)(await cmd.ExecuteScalarAsync(ct))!;
            _hasWrites = true;

            Diagnostics.KernelDiagnostics.EventsAppended.Add(1,
                new KeyValuePair<string, object?>("papuma.event_type", metadata.Name));
            return seq;
        }, ct);
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
    }
}
