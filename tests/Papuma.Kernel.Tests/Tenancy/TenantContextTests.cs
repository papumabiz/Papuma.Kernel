// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public class ScopeContextTests
{
    [Fact]
    public void Tenant_AllowsValidTenantId()
    {
        var scope = ScopeContext.Tenant("Acme_01");

        Assert.Equal(ScopeType.Tenant, scope.Scope);
        Assert.Equal("Acme_01", scope.TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1tenant")]
    [InlineData("tenant-name")]
    public void Tenant_RejectsInvalidTenantId(string tenantId)
    {
        var exception = Assert.Throws<ArgumentException>(() => ScopeContext.Tenant(tenantId));

        Assert.Equal("tenantId", exception.ParamName);
    }

    [Fact]
    public void Platform_UsesNullTenantId()
    {
        var scope = ScopeContext.Platform();

        Assert.Equal(ScopeType.Platform, scope.Scope);
        Assert.Null(scope.TenantId);
    }
}