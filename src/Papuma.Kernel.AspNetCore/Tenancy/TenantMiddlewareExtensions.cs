// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Provides DI registration, middleware and request extensions for tenant context handling.
/// </summary>
public static class TenantMiddlewareExtensions
{
    internal const string TenantContextItemKey = "Papuma.Kernel.Tenancy.TenantContext";

    /// <summary>
    /// Registers the specified <typeparamref name="TResolver"/> as the
    /// <see cref="ITenantResolver"/> implementation (scoped lifetime).
    /// </summary>
    /// <typeparam name="TResolver">The concrete tenant resolver type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddPapumaTenancy<TResolver>(this IServiceCollection services)
        where TResolver : class, ITenantResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ITenantResolver, TResolver>();
        return services;
    }

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