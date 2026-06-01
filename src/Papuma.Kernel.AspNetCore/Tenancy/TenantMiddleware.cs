// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Http;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Resolves and stores the tenant context for the current HTTP request.
/// </summary>
public sealed class TenantMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public TenantMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Resolves tenant context and stores it in <see cref="HttpContext.Items"/>.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="resolver">The tenant resolver implementation.</param>
    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);

        var tenant = resolver.Resolve(context);
        context.Items[TenantMiddlewareExtensions.TenantContextItemKey] = tenant;
        await _next(context);
    }
}