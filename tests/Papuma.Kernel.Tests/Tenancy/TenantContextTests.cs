// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public class TenantContextTests
{
    [Fact]
    public void Create_AllowsValidTenantId()
    {
        var tenant = TenantContext.Create("Acme_01");

        Assert.Equal("Acme_01", tenant.TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1tenant")]
    [InlineData("tenant-name")]
    public void Create_RejectsInvalidTenantId(string tenantId)
    {
        var exception = Assert.Throws<ArgumentException>(() => TenantContext.Create(tenantId));

        Assert.Equal("tenantId", exception.ParamName);
    }

    [Fact]
    public void Default_UsesDefaultTenantId()
    {
        Assert.Equal("default", TenantContext.Default.TenantId);
    }
}