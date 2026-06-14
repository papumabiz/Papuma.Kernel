// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Defines a scope filter for background workers that read across scopes.
/// Unlike <see cref="ScopeContext"/> (which always identifies a concrete scope),
/// <see cref="ScopeFilter"/> can also represent "all scopes" for unscoped workers.
/// </summary>
public sealed record ScopeFilter
{
    /// <summary>
    /// Gets the underlying scope context, or <c>null</c> when the filter matches all scopes.
    /// </summary>
    public ScopeContext? Scope { get; }

    /// <summary>
    /// Gets a value indicating whether this filter matches all scopes.
    /// </summary>
    public bool IsAll => Scope is null;

    private ScopeFilter(ScopeContext? scope)
    {
        Scope = scope;
    }

    /// <summary>
    /// Creates a filter that matches all scopes (no scope restriction).
    /// </summary>
    public static ScopeFilter All() => new((ScopeContext?)null);

    /// <summary>
    /// Creates a filter that matches only platform-scoped records.
    /// </summary>
    public static ScopeFilter Platform() => new(ScopeContext.Platform());

    /// <summary>
    /// Creates a filter that matches only records for the specified tenant.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    public static ScopeFilter Tenant(string tenantId) => new(ScopeContext.Tenant(tenantId));

    /// <summary>
    /// Creates a filter from an existing scope context.
    /// </summary>
    /// <param name="scope">The scope context to filter by.</param>
    public static ScopeFilter FromScope(ScopeContext scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(scope);
    }
}
