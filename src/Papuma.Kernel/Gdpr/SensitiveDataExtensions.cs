// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Registers sensitive data services.
/// </summary>
public static class SensitiveDataExtensions
{
    /// <summary>
    /// Registers the PostgreSQL-backed sensitive data store and resolver.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddSensitiveDataStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<NpgsqlSensitiveDataStore>();
        services.AddSingleton<ISensitiveDataStore>(sp => sp.GetRequiredService<NpgsqlSensitiveDataStore>());
        services.AddSingleton<ISensitiveDataResolver>(sp => sp.GetRequiredService<NpgsqlSensitiveDataStore>());

        return services;
    }
}