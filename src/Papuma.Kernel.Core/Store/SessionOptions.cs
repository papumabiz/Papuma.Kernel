// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Optional metadata for a <c>DocumentSession</c>. All change records written
/// by the session carry these values in their <c>metadata</c> column (architecture §5).
/// </summary>
public sealed record SessionOptions
{
    /// <summary>
    /// Gets the correlation id connecting all writes of this session.
    /// Auto-generated when not set.
    /// </summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>Gets the optional causation id (e.g. the triggering command or event).</summary>
    public string? CausationId { get; init; }

    /// <summary>Gets the optional actor id (who performed the change).</summary>
    public string? ActorId { get; init; }

    /// <summary>
    /// Gets the optional causation type — the name of the command or action that triggered
    /// this session's writes (e.g. <c>"PlaceOrder"</c>, <c>"ImportBatch"</c>). Written into
    /// the JSONB <c>metadata</c> of every change and event record (ADR-018).
    /// </summary>
    public string? CausationType { get; init; }
}
