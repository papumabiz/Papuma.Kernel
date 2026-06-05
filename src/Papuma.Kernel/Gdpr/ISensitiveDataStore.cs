// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Provides versioned write and lifecycle operations for sensitive payloads.
/// </summary>
public interface ISensitiveDataStore
{
    /// <summary>
    /// Appends a new active sensitive payload version.
    /// </summary>
    Task<SensitiveDataVersion> AppendAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        int schemaVersion,
        string payloadJson,
        string actorId,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the latest stored version for the given reference.
    /// </summary>
    Task<SensitiveDataVersion?> GetLatestAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        CancellationToken ct = default);

    /// <summary>
    /// Appends a redacted version marker.
    /// </summary>
    Task MarkRedactedAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        string actorId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Appends a deleted version marker.
    /// </summary>
    Task MarkDeletedAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        string actorId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Appends a new version with updated legal hold state.
    /// </summary>
    Task SetLegalHoldAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        bool enabled,
        string actorId,
        string reason,
        CancellationToken ct = default);
}