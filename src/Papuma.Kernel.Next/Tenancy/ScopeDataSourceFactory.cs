// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Collections.Concurrent;

using Npgsql;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Caches one <see cref="NpgsqlDataSource"/> per tenant for database-per-tenant setups.
/// Uses an LRU eviction strategy to bound memory usage.
/// </summary>
/// <remarks>
/// <para>
/// Cache hits (tenant already known) are served lock-free via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Cache misses and eviction share a single lock so that the factory delegate is called at most once
/// per tenant and eviction never disposes a data source that is about to be returned to a caller.
/// </para>
/// </remarks>
public sealed class ScopeDataSourceFactory : IScopeDataSourceFactory, IDisposable
{
    /// <summary>
    /// Default maximum number of cached data sources before LRU eviction kicks in.
    /// </summary>
    public const int DefaultMaxCacheSize = 128;

    private readonly ConcurrentDictionary<string, CacheEntry> _dataSources = new();
    private readonly Func<string, string> _connectionStringResolver;
    private readonly int _maxCacheSize;
    // Protects both cache-miss insertion and LRU eviction so that:
    //   1. The factory delegate is called exactly once per tenant.
    //   2. Eviction never removes an entry that is being returned to a caller.
    private readonly Lock _writeLock = new();
    private long _accessCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopeDataSourceFactory"/> class.
    /// </summary>
    /// <param name="connectionStringResolver">Resolves tenant id to connection string.</param>
    /// <param name="maxCacheSize">Maximum number of cached data sources (default: 128).</param>
    public ScopeDataSourceFactory(Func<string, string> connectionStringResolver, int maxCacheSize = DefaultMaxCacheSize)
    {
        ArgumentNullException.ThrowIfNull(connectionStringResolver);

        if (maxCacheSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCacheSize), "MaxCacheSize must be at least 1.");
        }

        _connectionStringResolver = connectionStringResolver;
        _maxCacheSize = maxCacheSize;
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

        // Fast path: tenant already cached — no lock needed.
        if (_dataSources.TryGetValue(scope.TenantId, out var existing))
        {
            Interlocked.Exchange(ref existing.LastAccessOrder, Interlocked.Increment(ref _accessCounter));
            return existing.DataSource;
        }

        // Slow path: create and insert under lock so the factory is called exactly once
        // and eviction cannot remove the new entry before it is returned.
        lock (_writeLock)
        {
            // Re-check: another thread may have inserted while we waited for the lock.
            if (_dataSources.TryGetValue(scope.TenantId, out var raceWinner))
            {
                Interlocked.Exchange(ref raceWinner.LastAccessOrder, Interlocked.Increment(ref _accessCounter));
                return raceWinner.DataSource;
            }

            var connectionString = _connectionStringResolver(scope.TenantId);
            var entry = new CacheEntry(NpgsqlDataSource.Create(connectionString), Interlocked.Increment(ref _accessCounter));
            _dataSources[scope.TenantId] = entry;

            EvictIfNeeded();

            return entry.DataSource;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var entry in _dataSources.Values)
        {
            entry.DataSource.Dispose();
        }

        _dataSources.Clear();
    }

    // Must be called while _writeLock is held.
    private void EvictIfNeeded()
    {
        while (_dataSources.Count > _maxCacheSize)
        {
            var oldest = _dataSources
                .OrderBy(kvp => kvp.Value.LastAccessOrder)
                .First();

            if (_dataSources.TryRemove(oldest.Key, out var removed))
            {
                removed.DataSource.Dispose();
            }
        }
    }

    private sealed class CacheEntry(NpgsqlDataSource dataSource, long accessOrder)
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;
        public long LastAccessOrder = accessOrder;
    }
}
