// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Defines the data scope for a write/read operation.
/// </summary>
public enum ScopeType
{
    /// <summary>
    /// Platform-wide data not bound to a tenant.
    /// </summary>
    Platform = 0,

    /// <summary>
    /// Data bound to a concrete tenant.
    /// </summary>
    Tenant = 1,
}