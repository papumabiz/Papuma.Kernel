// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Tenancy;

namespace ShopMinimalApi;

/// <summary>
/// The sample runs single-tenant for readability: every request and worker uses the
/// tenant <c>demo</c>. A real application resolves the tenant per request (subdomain,
/// claim, header) via <see cref="IScopeResolver"/> — the wiring is identical.
/// </summary>
public static class SampleScope
{
    public static ScopeContext Tenant { get; } = ScopeContext.Tenant("demo");
}

/// <summary>Resolves every request to the demo tenant (replace in real apps).</summary>
public sealed class DemoScopeResolver : IScopeResolver
{
    public ScopeContext Resolve(HttpContext context) => SampleScope.Tenant;
}
