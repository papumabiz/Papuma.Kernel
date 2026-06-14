// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Tenancy;

namespace EventModeledSlices;

/// <summary>Single-tenant for readability; a real app resolves the tenant per request.</summary>
public static class SampleScope
{
    public static ScopeContext Tenant { get; } = ScopeContext.Tenant("demo");
}

public sealed class DemoScopeResolver : IScopeResolver
{
    public ScopeContext Resolve(HttpContext context) => SampleScope.Tenant;
}
