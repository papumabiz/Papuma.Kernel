// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Events;

/// <summary>
/// An event log consumer (ADR-013). Same engine guarantees as change handlers
/// (ADR-009/022): per-handler delivery in causal order, persisted checkpoints, retry with
/// backoff, at-least-once delivery — implementations must be idempotent.
/// </summary>
/// <remarks>
/// <para>
/// As for change handlers (ADR-024): a handler that derives state also implements
/// <see cref="Processing.IProjection"/>; an effect handler is never reset — mark it
/// <see cref="Processing.StartsAtFeedHeadAttribute"/> so its first start does not act on
/// the whole history.
/// </para>
/// <para>
/// <b>Handlers see every scope.</b> The event log is read across all tenants and the platform
/// scope (<see cref="EventRecord.Scope"/> tells them apart), and no tenant context is set for the
/// call. A handler that reads or writes tenant data must establish the record's scope itself,
/// for example by opening its session with <see cref="EventRecord.Scope"/>, and must not carry
/// state from one record's tenant into the next.
/// </para>
/// </remarks>
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
