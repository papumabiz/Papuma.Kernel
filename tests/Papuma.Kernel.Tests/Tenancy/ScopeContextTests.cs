// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public sealed class ScopeContextTests
{
    [Fact]
    public void Platform_HasNoTenantId()
    {
        var scope = ScopeContext.Platform();

        Assert.Equal(ScopeType.Platform, scope.Scope);
        Assert.Null(scope.TenantId);
    }

    [Theory]
    [InlineData("tenant_a")]
    [InlineData("Acme01")]
    public void Tenant_AcceptsValidIds(string tenantId)
    {
        var scope = ScopeContext.Tenant(tenantId);

        Assert.Equal(ScopeType.Tenant, scope.Scope);
        Assert.Equal(tenantId, scope.TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1starts_with_digit")]
    [InlineData("has space")]
    [InlineData("x")] // too short: minimum two characters
    [InlineData("a'; DROP TABLE papuma.document; --")]
    public void Tenant_RejectsInvalidIds(string tenantId)
    {
        Assert.Throws<ArgumentException>(() => ScopeContext.Tenant(tenantId));
    }

    [Fact]
    public void ScopeFilter_All_HasNoScope()
    {
        var filter = ScopeFilter.All();

        Assert.True(filter.IsAll);
        Assert.Null(filter.Scope);
    }

    [Fact]
    public void ScopeFilter_Tenant_WrapsScopeContext()
    {
        var filter = ScopeFilter.Tenant("tenant_a");

        Assert.False(filter.IsAll);
        Assert.Equal(ScopeType.Tenant, filter.Scope!.Scope);
        Assert.Equal("tenant_a", filter.Scope.TenantId);
    }
}
