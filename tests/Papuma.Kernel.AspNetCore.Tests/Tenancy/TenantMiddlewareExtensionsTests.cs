// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tests.Tenancy;

public class TenantMiddlewareExtensionsTests
{
    [Fact]
    public void AddPapumaTenancy_NullServices_Throws()
    {
        IServiceCollection services = null!;
        Assert.Throws<ArgumentNullException>(() => services.AddPapumaTenancy<StubTenantResolver>());
    }

    [Fact]
    public void AddPapumaTenancy_RegistersResolver()
    {
        var services = new ServiceCollection();

        services.AddPapumaTenancy<StubTenantResolver>();

        var provider = services.BuildServiceProvider();
        var resolver = provider.GetService<ITenantResolver>();
        Assert.NotNull(resolver);
        Assert.IsType<StubTenantResolver>(resolver);
    }

    [Fact]
    public void AddPapumaTenancy_ReturnsSameCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddPapumaTenancy<StubTenantResolver>();

        Assert.Same(services, result);
    }

    [Fact]
    public void UseTenantResolution_NullApp_Throws()
    {
        Microsoft.AspNetCore.Builder.IApplicationBuilder app = null!;
        Assert.Throws<ArgumentNullException>(() => app.UseTenantResolution());
    }

    [Fact]
    public void GetTenantContext_WithoutMiddleware_Throws()
    {
        var context = new DefaultHttpContext();

        Assert.Throws<InvalidOperationException>(() => context.GetTenantContext());
    }

    [Fact]
    public void GetTenantContext_NullContext_Throws()
    {
        HttpContext context = null!;
        Assert.Throws<ArgumentNullException>(() => context.GetTenantContext());
    }

    [Fact]
    public void GetTenantContext_WithTenantSet_ReturnsTenant()
    {
        var context = new DefaultHttpContext();
        var tenant = TenantContext.Create("test_tenant");
        context.Items["Papuma.Kernel.Tenancy.TenantContext"] = tenant;

        var result = context.GetTenantContext();

        Assert.Equal(tenant, result);
    }

    private sealed class StubTenantResolver : ITenantResolver
    {
        public TenantContext Resolve(HttpContext context) =>
            TenantContext.Create("stub_tenant");
    }
}
