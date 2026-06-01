// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Resolves tenant-specific data sources for database-per-tenant setups.
/// </summary>
public interface ITenantDataSourceFactory
{
    /// <summary>
    /// Gets a data source for the supplied tenant context.
    /// </summary>
    /// <param name="tenant">The tenant context.</param>
    NpgsqlDataSource GetDataSource(TenantContext tenant);
}