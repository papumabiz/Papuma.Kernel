// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

using Papuma.Kernel.AspNetCore.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

/// <summary>
/// Review A-8: the default enricher bounds the actor id and derives the causation type
/// from explicit names only — never from the endpoint display name, which carries route
/// templates and assembly names.
/// </summary>
public sealed class ClaimsSessionOptionsEnricherTests
{
    private const string MinimalApiDisplayName = "HTTP: POST /orders/{id} => PlaceOrderHandler";

    private readonly ClaimsSessionOptionsEnricher _enricher = new();

    [Fact]
    public void ActorId_ComesFromTheNameIdentifierClaim()
    {
        var options = _enricher.Enrich(Context(
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim("sub", "other")));

        Assert.Equal("user-1", options.ActorId);
    }

    [Fact]
    public void ActorId_FallsBackToTheSubClaim()
    {
        var options = _enricher.Enrich(Context(new Claim("sub", "sub-1")));

        Assert.Equal("sub-1", options.ActorId);
    }

    [Fact]
    public void ActorId_IsNullWithoutClaim()
    {
        Assert.Null(_enricher.Enrich(Context()).ActorId);
    }

    [Fact]
    public void ActorId_AtTheLimit_IsAccepted()
    {
        var id = new string('a', ClaimsSessionOptionsEnricher.MaxActorIdLength);

        var options = _enricher.Enrich(Context(new Claim("sub", id)));

        Assert.Equal(id, options.ActorId);
    }

    [Fact]
    public void ActorId_BeyondTheLimit_IsRejected_WithoutEchoingTheValue()
    {
        var id = new string('a', ClaimsSessionOptionsEnricher.MaxActorIdLength + 1) + "SECRET-TAIL";

        var ex = Assert.Throws<InvalidOperationException>(() => _enricher.Enrich(Context(new Claim("sub", id))));

        Assert.Contains(id.Length.ToString(), ex.Message);
        Assert.DoesNotContain("SECRET-TAIL", ex.Message);
        Assert.True(ex.Message.Length < 400);
    }

    [Fact]
    public void CausationId_IsTheTraceIdentifier()
    {
        var context = Context();
        context.TraceIdentifier = "trace-42";

        Assert.Equal("trace-42", _enricher.Enrich(context).CausationId);
    }

    [Fact]
    public void CausationType_ComesFromTheEndpointName()
    {
        var context = Context();
        context.SetEndpoint(Endpoint(MinimalApiDisplayName, new EndpointNameMetadata("PlaceOrder")));

        Assert.Equal("PlaceOrder", _enricher.Enrich(context).CausationType);
    }

    [Fact]
    public void CausationType_ComesFromControllerAndActionName()
    {
        var context = Context();
        context.SetEndpoint(Endpoint(
            "Acme.Internal.Web.OrdersController.Get (Acme.Internal.Web)",
            new ControllerActionDescriptor { ControllerName = "Orders", ActionName = "Get" }));

        Assert.Equal("Orders.Get", _enricher.Enrich(context).CausationType);
    }

    [Fact]
    public void CausationType_PrefersTheEndpointNameOverTheAction()
    {
        var context = Context();
        context.SetEndpoint(Endpoint(
            "display",
            new ControllerActionDescriptor { ControllerName = "Orders", ActionName = "Get" },
            new EndpointNameMetadata("GetOrder")));

        Assert.Equal("GetOrder", _enricher.Enrich(context).CausationType);
    }

    [Fact]
    public void CausationType_IsNull_WhenTheEndpointHasNoExplicitName()
    {
        var context = Context();
        context.SetEndpoint(Endpoint(MinimalApiDisplayName));

        // The display name (route template, handler, assembly) must not leak into stored data.
        Assert.Null(_enricher.Enrich(context).CausationType);
    }

    [Fact]
    public void CausationType_IsNull_WithoutEndpoint()
    {
        Assert.Null(_enricher.Enrich(Context()).CausationType);
    }

    [Fact]
    public void Enrich_RejectsNullContext()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => _enricher.Enrich(null!));

        Assert.Equal("context", ex.ParamName);
    }

    private static DefaultHttpContext Context(params Claim[] claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")),
    };

    private static Microsoft.AspNetCore.Http.Endpoint Endpoint(string displayName, params object[] metadata) =>
        new(requestDelegate: null, new EndpointMetadataCollection(metadata), displayName);
}
