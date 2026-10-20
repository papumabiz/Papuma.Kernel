// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.AspNetCore.Http;

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

/// <summary>
/// Review A-5 / A-7: what <see cref="ScopeMiddleware"/> does when the resolver returns
/// nothing or rejects the tenant the client sent.
/// </summary>
public sealed class ScopeMiddlewareTests
{
    [Fact]
    public async Task ResolverReturningNull_ThrowsInvalidOperation_NamingTheResolver()
    {
        var (middleware, nextCalled) = Create();
        var context = new DefaultHttpContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(context, new NullResolver()));

        Assert.Contains(nameof(NullResolver), ex.Message);
        Assert.Contains("null", ex.Message);
        Assert.False(nextCalled());
        // The misleading follow-up ("Is ScopeMiddleware registered?") is not what the caller sees.
        Assert.DoesNotContain("ScopeMiddleware registered", ex.Message);
    }

    [Fact]
    public async Task ResolverRejectingTheTenant_AnswersBadRequest_AndDoesNotRunTheRestOfThePipeline()
    {
        var (middleware, nextCalled) = Create();
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, new HeaderResolver("X-Tenant", "bad tenant!"));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(nextCalled());
        Assert.Throws<InvalidOperationException>(() => context.GetScopeContext());
    }

    [Fact]
    public async Task ResolverAcceptingTheTenant_StoresTheScope_AndRunsTheRestOfThePipeline()
    {
        var (middleware, nextCalled) = Create();
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, new HeaderResolver("X-Tenant", "acme"));

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("acme", context.GetScopeContext().TenantId);
    }

    [Fact]
    public async Task ResolverThrowingArgumentNull_IsAProgrammingError_AndPropagates()
    {
        var (middleware, _) = Create();
        var context = new DefaultHttpContext();

        // A resolver that forgot to handle a missing claim: a bug, not a bad client request.
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => middleware.InvokeAsync(context, new ThrowingResolver(new ArgumentNullException("tenantId"))));
    }

    [Fact]
    public async Task ResolverThrowingOtherExceptions_Propagate()
    {
        var (middleware, _) = Create();
        var context = new DefaultHttpContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(context, new ThrowingResolver(new InvalidOperationException("boom"))));
    }

    [Fact]
    public async Task ArgumentExceptionFromTheRestOfThePipeline_IsNotTurnedIntoBadRequest()
    {
        var middleware = new ScopeMiddleware(_ => throw new ArgumentException("downstream bug", "x"));
        var context = new DefaultHttpContext();

        await Assert.ThrowsAsync<ArgumentException>(
            () => middleware.InvokeAsync(context, new HeaderResolver("X-Tenant", "acme")));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static (ScopeMiddleware Middleware, Func<bool> NextCalled) Create()
    {
        var called = false;
        var middleware = new ScopeMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        return (middleware, () => called);
    }

    private sealed class NullResolver : IScopeResolver
    {
        public ScopeContext Resolve(HttpContext context) => null!;
    }

    // Stands in for the typical resolver that feeds a client-controlled value to ScopeContext.Tenant.
    private sealed class HeaderResolver(string header, string value) : IScopeResolver
    {
        public ScopeContext Resolve(HttpContext context)
        {
            context.Request.Headers[header] = value;
            return ScopeContext.Tenant(context.Request.Headers[header].ToString());
        }
    }

    private sealed class ThrowingResolver(Exception exception) : IScopeResolver
    {
        public ScopeContext Resolve(HttpContext context) => throw exception;
    }
}
