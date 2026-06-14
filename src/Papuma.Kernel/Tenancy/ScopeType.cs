// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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