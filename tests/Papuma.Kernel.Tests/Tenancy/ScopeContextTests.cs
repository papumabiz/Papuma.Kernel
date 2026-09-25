// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
    [InlineData("42")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")] // GUID "D"
    [InlineData("3F2504E04F8911D39A0C0305E82C3301")]     // GUID "N", uppercase
    public void Tenant_AcceptsValidIds(string tenantId)
    {
        var scope = ScopeContext.Tenant(tenantId);

        Assert.Equal(ScopeType.Tenant, scope.Scope);
        Assert.Equal(tenantId, scope.TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-starts_with_dash")]
    [InlineData("_starts_with_underscore")]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")] // GUID "B": braces
    [InlineData("has space")]
    [InlineData("x")] // too short: minimum two characters
    [InlineData("a'; DROP TABLE papuma.document; --")]
    public void Tenant_RejectsInvalidIds(string tenantId)
    {
        Assert.Throws<ArgumentException>(() => ScopeContext.Tenant(tenantId));
    }

    [Fact]
    public void Tenant_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ScopeContext.Tenant((string)null!));
    }

    [Fact]
    public void Tenant_FromGuid_UsesCanonicalLowercaseDashedForm()
    {
        var guid = Guid.Parse("3F2504E0-4F89-11D3-9A0C-0305E82C3301");

        var scope = ScopeContext.Tenant(guid);

        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", scope.TenantId);
        Assert.Equal(scope, ScopeContext.Tenant(guid.ToString("D")));
    }

    [Fact]
    public void Tenant_FromGuid_RejectsEmptyGuid()
    {
        var exception = Assert.Throws<ArgumentException>(() => ScopeContext.Tenant(Guid.Empty));

        Assert.Equal("tenantId", exception.ParamName);
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
