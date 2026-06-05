// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;

namespace Papuma.Kernel.Schema;

/// <summary>
/// Registers schema-related services.
/// </summary>
public static class SchemaExtensions
{
    /// <summary>
    /// Registers <see cref="SchemaVersionChecker"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The original service collection for chaining.</returns>
    public static IServiceCollection AddSchemaVersionChecker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<SchemaVersionChecker>();
        return services;
    }
}