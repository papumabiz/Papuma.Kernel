// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public class ScopeDataSourceFactoryTests
{
    private const string ConnString = "Host=localhost;Port=1;Database=test;Username=test;Password=test";

    [Fact]
    public void Constructor_ThrowsForNullResolver()
    {
        Assert.Throws<ArgumentNullException>(() => new ScopeDataSourceFactory(connectionStringResolver: null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ThrowsForInvalidMaxCacheSize(int maxCacheSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScopeDataSourceFactory(_ => ConnString, maxCacheSize));
    }

    [Fact]
    public void GetDataSource_ThrowsForNullScope()
    {
        using var sut = new ScopeDataSourceFactory(_ => ConnString);

        Assert.Throws<ArgumentNullException>(() => sut.GetDataSource(scope: null!));
    }

    [Fact]
    public void GetDataSource_ThrowsForPlatformScope()
    {
        using var sut = new ScopeDataSourceFactory(_ => ConnString);

        Assert.Throws<ArgumentException>(() => sut.GetDataSource(ScopeContext.Platform()));
    }

    [Fact]
    public void GetDataSource_CachesPerTenantId()
    {
        using var sut = new ScopeDataSourceFactory(_ => ConnString);
        var scope = ScopeContext.Tenant("Acme");

        var first = sut.GetDataSource(scope);
        var second = sut.GetDataSource(scope);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetDataSource_EvictsLeastRecentlyUsedWhenCacheFull()
    {
        using var sut = new ScopeDataSourceFactory(_ => ConnString, maxCacheSize: 2);

        // Fill cache with two entries.
        var ds1 = sut.GetDataSource(ScopeContext.Tenant("T1"));
        var ds2 = sut.GetDataSource(ScopeContext.Tenant("T2"));

        // Access T1 again so T2 becomes the LRU entry.
        _ = sut.GetDataSource(ScopeContext.Tenant("T1"));

        // Adding T3 should evict T2 (least recently used).
        var ds3 = sut.GetDataSource(ScopeContext.Tenant("T3"));

        // T1 should still be cached (same instance).
        var ds1Again = sut.GetDataSource(ScopeContext.Tenant("T1"));
        Assert.Same(ds1, ds1Again);

        // T3 should be cached.
        var ds3Again = sut.GetDataSource(ScopeContext.Tenant("T3"));
        Assert.Same(ds3, ds3Again);

        // T2 was evicted — a new instance should be created.
        var ds2New = sut.GetDataSource(ScopeContext.Tenant("T2"));
        Assert.NotSame(ds2, ds2New);
    }

    [Fact]
    public void DefaultMaxCacheSize_Is128()
    {
        Assert.Equal(128, ScopeDataSourceFactory.DefaultMaxCacheSize);
    }
}