// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Routes versioned event payloads to registered projection handlers.
/// </summary>
/// <remarks>
/// Unknown event types or versions are silently skipped with a warning log entry.
/// This allows rolling deployments where new event types are introduced before all
/// projections are updated.
/// </remarks>
public sealed class ProjectionRegistry
{
    private readonly Dictionary<(string EventType, int Version), Func<ChangeRecord, CancellationToken, Task>> _handlers = new();
    private readonly ILogger<ProjectionRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectionRegistry"/> class.
    /// </summary>
    /// <param name="logger">
    /// Optional logger for unknown-event warnings. When <see langword="null"/>,
    /// a no-op logger is used.
    /// </param>
    public ProjectionRegistry(ILogger<ProjectionRegistry>? logger = null)
    {
        _logger = logger ?? NullLogger<ProjectionRegistry>.Instance;
    }

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
    /// Unknown event types or versions are skipped with a warning log entry.
    /// </summary>
    /// <param name="record">The record to dispatch.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="record"/> has no version set (i.e. it is not a Change record).
    /// </exception>
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

        // No handler registered — skip silently and log a warning.
        // This is expected during rolling deployments when a new event type is introduced
        // before all projections are updated.
        _logger.LogWarning(
            "No handler registered for event '{EventType}' version {Version} (sequence {SequenceId}). Skipping.",
            record.EventType,
            version,
            record.SequenceId);
    }
}
