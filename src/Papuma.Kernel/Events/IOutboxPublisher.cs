// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Events;

using Papuma.Kernel.Tenancy;

/// <summary>
/// Publishes pending outbox messages to an external system (message broker, webhook, etc.).
/// Implementations must be idempotent because messages may be delivered more than once.
/// </summary>
public interface IOutboxPublisher
{
    /// <summary>
    /// Publishes a single outbox message to the external target.
    /// </summary>
    /// <param name="scope">The scope context for the message.</param>
    /// <param name="eventId">The unique event identifier.</param>
    /// <param name="eventType">The business event type.</param>
    /// <param name="payloadJson">The JSON payload to publish.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>true</c> if the message was published successfully; otherwise <c>false</c>.</returns>
    Task<bool> PublishAsync(
        ScopeContext scope,
        Guid eventId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default);
}
