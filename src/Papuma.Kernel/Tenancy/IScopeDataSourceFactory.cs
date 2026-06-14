// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
