// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Model;

/// <summary>
/// The startup-built metadata of one registered event type (ADR-013): name, field
/// policies for the payload, and optional retention.
/// </summary>
/// <remarks>
/// Events are immutable facts — there is deliberately no upcaster chain (ADR-013):
/// transforming changes require a new event type, payload reading follows the
/// additive rules of ADR-005.
/// </remarks>
public sealed class EventTypeMetadata
{
    /// <summary>Gets the logical event type name (table column <c>event_type</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>
    /// Gets non-default field policies keyed by dot-separated JSON path.
    /// Paths not present default to <see cref="FieldPolicy.Track"/>.
    /// </summary>
    public IReadOnlyDictionary<string, FieldPolicy> Policies { get; }

    /// <summary>
    /// Gets the optional retention period — events older than this may be purged
    /// (ADR-013: the event log is a fact store, not a version store).
    /// </summary>
    public TimeSpan? Retention { get; }

    internal EventTypeMetadata(
        string name,
        Type clrType,
        IReadOnlyDictionary<string, FieldPolicy> policies,
        TimeSpan? retention)
    {
        Name = name;
        ClrType = clrType;
        Policies = policies;
        Retention = retention;
    }
}
