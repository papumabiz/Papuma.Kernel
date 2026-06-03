// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Resolves scope-specific data sources for database-per-tenant setups.
/// </summary>
public interface IScopeDataSourceFactory
{
    /// <summary>
    /// Gets a data source for the supplied scope context.
    /// </summary>
    /// <param name="scope">The scope context.</param>
    NpgsqlDataSource GetDataSource(ScopeContext scope);
}
