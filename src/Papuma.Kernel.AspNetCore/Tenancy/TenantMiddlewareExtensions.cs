// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Provides middleware and request extensions for tenant context handling.
/// </summary>
public static class TenantMiddlewareExtensions
{
    internal const string TenantContextItemKey = "Papuma.Kernel.Tenancy.TenantContext";

    /// <summary>
    /// Adds tenant resolution middleware to the request pipeline.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantMiddleware>();
    }

    /// <summary>
    /// Gets the resolved tenant context for the current request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The resolved tenant context.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no tenant context was set for the request.</exception>
    public static TenantContext GetTenantContext(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(TenantContextItemKey, out var value) && value is TenantContext tenant)
        {
            return tenant;
        }

        throw new InvalidOperationException("TenantContext not set. Is TenantMiddleware registered?");
    }
}