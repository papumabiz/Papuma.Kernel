// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public class ScopeFilterTests
{
    [Fact]
    public void All_ReturnsFilterWithNullScope()
    {
        var filter = ScopeFilter.All();

        Assert.True(filter.IsAll);
        Assert.Null(filter.Scope);
    }

    [Fact]
    public void Platform_ReturnsFilterWithPlatformScope()
    {
        var filter = ScopeFilter.Platform();

        Assert.False(filter.IsAll);
        Assert.NotNull(filter.Scope);
        Assert.Equal(ScopeType.Platform, filter.Scope!.Scope);
        Assert.Null(filter.Scope.TenantId);
    }

    [Fact]
    public void Tenant_ReturnsFilterWithTenantScope()
    {
        var filter = ScopeFilter.Tenant("Acme");

        Assert.False(filter.IsAll);
        Assert.NotNull(filter.Scope);
        Assert.Equal(ScopeType.Tenant, filter.Scope!.Scope);
        Assert.Equal("Acme", filter.Scope.TenantId);
    }

    [Fact]
    public void FromScope_ReturnsFilterWithGivenScope()
    {
        var scope = ScopeContext.Tenant("Beta");
        var filter = ScopeFilter.FromScope(scope);

        Assert.False(filter.IsAll);
        Assert.Same(scope, filter.Scope);
    }

    [Fact]
    public void FromScope_ThrowsForNullScope()
    {
        Assert.Throws<ArgumentNullException>(() => ScopeFilter.FromScope(null!));
    }

    [Fact]
    public void Tenant_DelegatesToScopeContext_ThrowsForEmptyTenantId()
    {
        Assert.Throws<ArgumentException>(() => ScopeFilter.Tenant(""));
    }

    [Fact]
    public void Equality_TwoAllFiltersAreEqual()
    {
        var a = ScopeFilter.All();
        var b = ScopeFilter.All();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_TwoTenantFiltersWithSameIdAreEqual()
    {
        var a = ScopeFilter.Tenant("Acme");
        var b = ScopeFilter.Tenant("Acme");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_AllAndTenantAreNotEqual()
    {
        var all = ScopeFilter.All();
        var tenant = ScopeFilter.Tenant("Acme");

        Assert.NotEqual(all, tenant);
    }
}
