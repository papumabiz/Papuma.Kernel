// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// DI registration and <see cref="HttpContext"/> extensions for automatic
/// <see cref="SessionOptions"/> enrichment (ADR-018).
/// </summary>
public static class SessionOptionsExtensions
{
    /// <summary>
    /// Registers the specified <typeparamref name="TEnricher"/> as the
    /// <see cref="ISessionOptionsEnricher"/> implementation (scoped lifetime).
    /// </summary>
    /// <typeparam name="TEnricher">The concrete enricher type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddPapumaSessionOptions<TEnricher>(this IServiceCollection services)
        where TEnricher : class, ISessionOptionsEnricher
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ISessionOptionsEnricher, TEnricher>();
        return services;
    }

    /// <summary>
    /// Builds <see cref="SessionOptions"/> for the current request using the registered
    /// <see cref="ISessionOptionsEnricher"/>. Falls back to empty options when no enricher
    /// is registered.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The enriched session options.</returns>
    public static SessionOptions GetSessionOptions(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var enricher = context.RequestServices.GetService<ISessionOptionsEnricher>();
        return enricher?.Enrich(context) ?? new SessionOptions();
    }
}
