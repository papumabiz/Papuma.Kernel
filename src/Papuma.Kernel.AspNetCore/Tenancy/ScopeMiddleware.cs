// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Http;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Resolves and stores the scope context for the current HTTP request.
/// </summary>
public sealed class ScopeMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopeMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public ScopeMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Resolves scope context and stores it in <see cref="HttpContext.Items"/>.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="resolver">The scope resolver implementation.</param>
    public async Task InvokeAsync(HttpContext context, IScopeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);

        var scope = resolver.Resolve(context);
        context.Items[ScopeMiddlewareExtensions.ScopeContextItemKey] = scope;
        await _next(context);
    }
}
