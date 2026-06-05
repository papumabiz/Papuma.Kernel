// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Resolves sensitive payloads for read-side processing.
/// </summary>
public interface ISensitiveDataResolver
{
    /// <summary>
    /// Resolves the latest payload for a sensitive reference when it is still active.
    /// Returns <c>null</c> when the reference is missing, redacted, or deleted.
    /// </summary>
    Task<string?> TryResolveLatestPayloadAsync(
        ScopeContext scope,
        SensitiveRef sensitiveRef,
        CancellationToken ct = default);
}