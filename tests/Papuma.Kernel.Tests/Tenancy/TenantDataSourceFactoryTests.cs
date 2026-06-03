// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public class ScopeDataSourceFactoryTests
{
    [Fact]
    public void Constructor_ThrowsForNullResolver()
    {
        Assert.Throws<ArgumentNullException>(() => new ScopeDataSourceFactory(connectionStringResolver: null!));
    }

    [Fact]
    public void GetDataSource_ThrowsForNullScope()
    {
        using var sut = new ScopeDataSourceFactory(_ =>
            "Host=localhost;Port=1;Database=test;Username=test;Password=test");

        Assert.Throws<ArgumentNullException>(() => sut.GetDataSource(scope: null!));
    }

    [Fact]
    public void GetDataSource_ThrowsForPlatformScope()
    {
        using var sut = new ScopeDataSourceFactory(_ =>
            "Host=localhost;Port=1;Database=test;Username=test;Password=test");

        Assert.Throws<ArgumentException>(() => sut.GetDataSource(ScopeContext.Platform()));
    }

    [Fact]
    public void GetDataSource_CachesPerTenantId()
    {
        using var sut = new ScopeDataSourceFactory(_ =>
            "Host=localhost;Port=1;Database=test;Username=test;Password=test");
        var scope = ScopeContext.Tenant("Acme");

        var first = sut.GetDataSource(scope);
        var second = sut.GetDataSource(scope);

        Assert.Same(first, second);
    }
}