// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Represents a stable reference to versioned sensitive data.
/// </summary>
public readonly record struct SensitiveRef(Guid Value)
{
    /// <summary>
    /// Creates a new random sensitive reference.
    /// </summary>
    public static SensitiveRef New() => new(Guid.NewGuid());

    /// <summary>
    /// Returns a value indicating whether this reference is empty.
    /// </summary>
    public bool IsEmpty => Value == Guid.Empty;
}