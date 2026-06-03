// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Http;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Resolves the scope context from an ASP.NET Core request.
/// </summary>
public interface IScopeResolver
{
    /// <summary>
    /// Resolves the scope context for the specified request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    ScopeContext Resolve(HttpContext context);
}
