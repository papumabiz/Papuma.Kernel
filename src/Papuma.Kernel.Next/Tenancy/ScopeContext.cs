// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.RegularExpressions;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Represents the current data scope for platform- or tenant-scoped operations.
/// </summary>
public sealed record ScopeContext
{
    private static readonly Regex ValidTenantPattern = new(
        "^[A-Za-z][A-Za-z0-9_]{1,100}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Gets the resolved scope kind.
    /// </summary>
    public ScopeType Scope { get; }

    /// <summary>
    /// Gets the tenant identifier for <see cref="ScopeType.Tenant"/> scope; otherwise <c>null</c>.
    /// </summary>
    public string? TenantId { get; }

    private ScopeContext(ScopeType scope, string? tenantId)
    {
        Scope = scope;
        TenantId = tenantId;
    }

    /// <summary>
    /// Creates a platform scope.
    /// </summary>
    public static ScopeContext Platform() => new(ScopeType.Platform, null);

    /// <summary>
    /// Creates a validated tenant scope.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    public static ScopeContext Tenant(string tenantId)
    {
        if (!ValidTenantPattern.IsMatch(tenantId))
        {
            throw new ArgumentException(
                $"Invalid tenantId '{tenantId}'. Must match [A-Za-z][A-Za-z0-9_]{{1,100}}.",
                nameof(tenantId));
        }

        return new ScopeContext(ScopeType.Tenant, tenantId);
    }
}