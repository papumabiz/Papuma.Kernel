// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Model;

/// <summary>
/// Thrown when an event type is used that was not registered in the kernel model.
/// </summary>
public sealed class EventTypeNotRegisteredException : InvalidOperationException
{
    /// <summary>Gets the unregistered CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventTypeNotRegisteredException"/> class.
    /// </summary>
    /// <param name="clrType">The unregistered CLR type.</param>
    public EventTypeNotRegisteredException(Type clrType)
        : base($"Event type {clrType.FullName} is not registered. " +
               "Register it via KernelModelBuilder.Event<T>() before appending events.")
    {
        ClrType = clrType;
    }
}
