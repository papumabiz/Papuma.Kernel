// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.ChangeFeed;

using Papuma.Kernel.Tenancy;

/// <summary>
/// Represents a persisted change feed entry.
/// </summary>
public sealed record ChangeRecord(
    /// <summary>
    /// Gets the sequence identifier assigned by the store.
    /// </summary>
    long SequenceId,
    /// <summary>
    /// Gets the logical entity name that produced the change.
    /// </summary>
    string Entity,
    /// <summary>
    /// Gets the entity identifier within its logical namespace.
    /// </summary>
    string EntityId,
    /// <summary>
    /// Gets the event type that describes the change.
    /// </summary>
    string EventType,
    /// <summary>
    /// Gets the aggregate version associated with the change.
    /// </summary>
    int Version,
    /// <summary>
    /// Gets the optional correlation identifier.
    /// </summary>
    string? CorrelationId,
    /// <summary>
    /// Gets the optional causation identifier.
    /// </summary>
    string? CausationId,
    /// <summary>
    /// Gets the actor that caused the change.
    /// </summary>
    string ActorId,
    /// <summary>
    /// Gets the JSON payload associated with the change.
    /// </summary>
    string PayloadJson,
    /// <summary>
    /// Gets the timestamp at which the change was recorded.
    /// </summary>
    DateTimeOffset Timestamp,
    /// <summary>
    /// Gets the scope associated with the change.
    /// </summary>
    ScopeType Scope,
    /// <summary>
    /// Gets the tenant identifier associated with the change for tenant scope; otherwise <c>null</c>.
    /// </summary>
    string? TenantId);