// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Provides DI registration, middleware and request extensions for scope context handling.
/// </summary>
public static class ScopeMiddlewareExtensions
{
    internal const string ScopeContextItemKey = "Papuma.Kernel.Tenancy.ScopeContext";

    /// <summary>
    /// Registers the specified <typeparamref name="TResolver"/> as the
    /// <see cref="IScopeResolver"/> implementation (scoped lifetime).
    /// </summary>
    /// <typeparam name="TResolver">The concrete scope resolver type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddPapumaScope<TResolver>(this IServiceCollection services)
        where TResolver : class, IScopeResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IScopeResolver, TResolver>();
        return services;
    }

    /// <summary>
    /// Adds scope resolution middleware to the request pipeline.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseScopeResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ScopeMiddleware>();
    }

    /// <summary>
    /// Gets the resolved scope context for the current request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The resolved scope context.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no scope context was set for the request.</exception>
    public static ScopeContext GetScopeContext(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(ScopeContextItemKey, out var value) && value is ScopeContext scope)
        {
            return scope;
        }

        throw new InvalidOperationException("ScopeContext not set. Is ScopeMiddleware registered?");
    }
}
