// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Events;

/// <summary>
/// Typed sugar over <see cref="EventRecord"/>, the counterpart of <c>ChangeRecordExtensions</c>.
/// </summary>
public static class EventRecordExtensions
{
    /// <summary>
    /// Returns whether this record is an event of type <typeparamref name="TEvent"/>
    /// (e.g. <c>if (!@event.Is&lt;OrderPaid&gt;(model)) return;</c>). Compares the record's logical name with the one
    /// the model gives <typeparamref name="TEvent"/> — from <c>[EventType]</c>, <c>.Named(…)</c> or the class name
    /// (ADR-028) — so the check keeps working when the type gets a logical name of its own.
    /// </summary>
    /// <typeparam name="TEvent">The event CLR type.</typeparam>
    /// <param name="event">The event record.</param>
    /// <param name="model">The kernel model the event was appended with.</param>
    /// <exception cref="EventTypeNotRegisteredException"><typeparamref name="TEvent"/> is not registered in <paramref name="model"/>.</exception>
    public static bool Is<TEvent>(this EventRecord @event, KernelModel model)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(model);
        return string.Equals(@event.EventType, model.GetRequiredEvent<TEvent>().Name, StringComparison.Ordinal);
    }
}
