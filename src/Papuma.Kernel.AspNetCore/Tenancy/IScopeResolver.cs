// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
