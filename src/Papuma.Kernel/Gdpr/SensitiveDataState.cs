// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Describes the lifecycle state of a sensitive data version.
/// </summary>
public enum SensitiveDataState
{
    /// <summary>
    /// Payload is available for resolution.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Payload was redacted and should no longer be exposed.
    /// </summary>
    Redacted = 1,

    /// <summary>
    /// Payload was deleted and should be treated as unavailable.
    /// </summary>
    Deleted = 2,
}