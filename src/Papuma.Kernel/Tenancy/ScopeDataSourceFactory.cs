// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Collections.Concurrent;

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Caches one <see cref="NpgsqlDataSource"/> per tenant for database-per-tenant setups.
/// </summary>
public sealed class ScopeDataSourceFactory : IScopeDataSourceFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _dataSources = new();
    private readonly Func<string, string> _connectionStringResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopeDataSourceFactory"/> class.
    /// </summary>
    /// <param name="connectionStringResolver">Resolves tenant id to connection string.</param>
    public ScopeDataSourceFactory(Func<string, string> connectionStringResolver)
    {
        ArgumentNullException.ThrowIfNull(connectionStringResolver);
        _connectionStringResolver = connectionStringResolver;
    }

    /// <inheritdoc />
    public NpgsqlDataSource GetDataSource(ScopeContext scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.Scope != ScopeType.Tenant || string.IsNullOrWhiteSpace(scope.TenantId))
        {
            throw new ArgumentException(
                "Database-per-tenant data sources require a tenant scope with a tenant id.",
                nameof(scope));
        }

        return _dataSources.GetOrAdd(scope.TenantId, tenantId =>
        {
            var connectionString = _connectionStringResolver(tenantId);
            return NpgsqlDataSource.Create(connectionString);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var dataSource in _dataSources.Values)
        {
            dataSource.Dispose();
        }

        _dataSources.Clear();
    }
}
