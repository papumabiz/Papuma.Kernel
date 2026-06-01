// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public class TenantDataSourceFactoryTests
{
    [Fact]
    public void Constructor_ThrowsForNullResolver()
    {
        Assert.Throws<ArgumentNullException>(() => new TenantDataSourceFactory(connectionStringResolver: null!));
    }

    [Fact]
    public void GetDataSource_ThrowsForNullTenant()
    {
        using var sut = new TenantDataSourceFactory(_ =>
            "Host=localhost;Port=1;Database=test;Username=test;Password=test");

        Assert.Throws<ArgumentNullException>(() => sut.GetDataSource(tenant: null!));
    }

    [Fact]
    public void GetDataSource_CachesPerTenantId()
    {
        using var sut = new TenantDataSourceFactory(_ =>
            "Host=localhost;Port=1;Database=test;Username=test;Password=test");
        var tenant = TenantContext.Create("Acme");

        var first = sut.GetDataSource(tenant);
        var second = sut.GetDataSource(tenant);

        Assert.Same(first, second);
    }
}