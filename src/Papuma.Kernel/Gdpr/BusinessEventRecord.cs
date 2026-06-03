// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Gdpr;

using Papuma.Kernel.Tenancy;

/// <summary>
/// Represents a business event entry returned for GDPR history requests.
/// </summary>
public sealed record BusinessEventRecord(
    Guid EventId,
    string EventType,
    string ActorId,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    ScopeType Scope,
    string? TenantId);