// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Store;

/// <summary>
/// Optional metadata for a <see cref="DocumentSession"/>. All change records written
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
}
