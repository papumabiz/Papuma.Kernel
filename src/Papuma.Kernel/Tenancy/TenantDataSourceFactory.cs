// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Collections.Concurrent;

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Caches one <see cref="NpgsqlDataSource"/> per tenant for database-per-tenant setups.
/// </summary>
public sealed class TenantDataSourceFactory : ITenantDataSourceFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _dataSources = new();
    private readonly Func<string, string> _connectionStringResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantDataSourceFactory"/> class.
    /// </summary>
    /// <param name="connectionStringResolver">Resolves tenant id to connection string.</param>
    public TenantDataSourceFactory(Func<string, string> connectionStringResolver)
    {
        ArgumentNullException.ThrowIfNull(connectionStringResolver);
        _connectionStringResolver = connectionStringResolver;
    }

    /// <inheritdoc />
    public NpgsqlDataSource GetDataSource(TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return _dataSources.GetOrAdd(tenant.TenantId, tenantId =>
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