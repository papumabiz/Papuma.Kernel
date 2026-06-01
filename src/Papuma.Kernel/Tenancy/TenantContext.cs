// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.RegularExpressions;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Represents the current tenant context for tenant-scoped operations.
/// </summary>
public sealed record TenantContext
{
    private static readonly Regex ValidTenantPattern = new(
        "^[A-Za-z][A-Za-z0-9_]{1,100}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Gets the default tenant context used by backward-compatible overloads.
    /// </summary>
    public static TenantContext Default { get; } = new("default");

    /// <summary>
    /// Gets the tenant identifier.
    /// </summary>
    public string TenantId { get; }

    private TenantContext(string tenantId)
    {
        TenantId = tenantId;
    }

    /// <summary>
    /// Creates a validated tenant context.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    public static TenantContext Create(string tenantId)
    {
        if (!ValidTenantPattern.IsMatch(tenantId))
        {
            throw new ArgumentException(
                $"Invalid tenantId '{tenantId}'. Must match [A-Za-z][A-Za-z0-9_]{{1,100}}.",
                nameof(tenantId));
        }

        return new TenantContext(tenantId);
    }
}