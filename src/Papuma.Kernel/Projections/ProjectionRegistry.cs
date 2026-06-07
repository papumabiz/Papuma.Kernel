// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Routes versioned event payloads to registered projection handlers.
/// </summary>
public sealed class ProjectionRegistry
{
    private readonly Dictionary<(string EventType, int Version), Func<ChangeRecord, CancellationToken, Task>> _handlers = new();

    /// <summary>
    /// Registers a versioned handler for an event type.
    /// </summary>
    /// <typeparam name="TPayload">The payload type expected by the handler.</typeparam>
    /// <param name="eventType">The event type handled by the projection.</param>
    /// <param name="handler">The version-specific handler.</param>
    public void Register<TPayload>(string eventType, IVersionedHandler<TPayload> handler)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handler);

        var key = (eventType, handler.Version);
        _handlers[key] = async (record, ct) =>
        {
            var data = JsonSerializer.Deserialize<TPayload>(record.PayloadJson)
                ?? throw new InvalidOperationException("Deserialization returned null.");

            await handler.HandleAsync(data, record, ct);
        };
    }

    /// <summary>
    /// Dispatches a change record to its matching versioned handler.
    /// </summary>
    /// <param name="record">The record to dispatch.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task DispatchAsync(ChangeRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var version = record.Version
            ?? throw new InvalidOperationException(
                $"Event '{record.EventType}' has no version set. Only Change records can be dispatched through the registry.");

        var key = (record.EventType, version);
        if (_handlers.TryGetValue(key, out var handle))
        {
            await handle(record, ct);
            return;
        }

        throw new InvalidOperationException(
            $"No handler registered for event '{record.EventType}' version {version}.");
    }
}
