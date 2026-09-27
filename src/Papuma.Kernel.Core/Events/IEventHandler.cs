// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Events;

/// <summary>
/// An event log consumer (ADR-013). Same engine guarantees as change handlers
/// (ADR-009/022): per-handler delivery in causal order, persisted checkpoints, retry with
/// backoff, at-least-once delivery — implementations must be idempotent.
/// </summary>
public interface IEventHandler
{
    /// <summary>Gets the unique, stable handler name (checkpoint key).</summary>
    string Name { get; }

    /// <summary>
    /// Applies a single event. Throw to trigger retry with backoff; after the configured
    /// maximum attempts the event is skipped (poison) and stays recorded in the failure table.
    /// </summary>
    /// <param name="event">The event record.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleAsync(EventRecord @event, CancellationToken ct);
}
