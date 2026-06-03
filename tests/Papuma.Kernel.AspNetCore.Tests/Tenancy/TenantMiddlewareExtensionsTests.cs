// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tests.Tenancy;

public class ScopeMiddlewareExtensionsTests
{
    [Fact]
    public void AddPapumaScope_NullServices_Throws()
    {
        IServiceCollection services = null!;
        Assert.Throws<ArgumentNullException>(() => services.AddPapumaScope<StubScopeResolver>());
    }

    [Fact]
    public void AddPapumaScope_RegistersResolver()
    {
        var services = new ServiceCollection();

        services.AddPapumaScope<StubScopeResolver>();

        var provider = services.BuildServiceProvider();
        var resolver = provider.GetService<IScopeResolver>();
        Assert.NotNull(resolver);
        Assert.IsType<StubScopeResolver>(resolver);
    }

    [Fact]
    public void AddPapumaScope_ReturnsSameCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddPapumaScope<StubScopeResolver>();

        Assert.Same(services, result);
    }

    [Fact]
    public void UseScopeResolution_NullApp_Throws()
    {
        Microsoft.AspNetCore.Builder.IApplicationBuilder app = null!;
        Assert.Throws<ArgumentNullException>(() => app.UseScopeResolution());
    }

    [Fact]
    public void GetScopeContext_WithoutMiddleware_Throws()
    {
        var context = new DefaultHttpContext();

        Assert.Throws<InvalidOperationException>(() => context.GetScopeContext());
    }

    [Fact]
    public void GetScopeContext_NullContext_Throws()
    {
        HttpContext context = null!;
        Assert.Throws<ArgumentNullException>(() => context.GetScopeContext());
    }

    [Fact]
    public void GetScopeContext_WithScopeSet_ReturnsScope()
    {
        var context = new DefaultHttpContext();
        var scope = ScopeContext.Tenant("test_tenant");
        context.Items["Papuma.Kernel.Tenancy.ScopeContext"] = scope;

        var result = context.GetScopeContext();

        Assert.Equal(scope, result);
    }

    private sealed class StubScopeResolver : IScopeResolver
    {
        public ScopeContext Resolve(HttpContext context) =>
            ScopeContext.Tenant("stub_tenant");
    }
}
