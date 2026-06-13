// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Store;

namespace EventModeledSlices.Features.PlaceOrder;

/// <summary>The endpoint — invariant shape: bind, resolve scope, call the handler.</summary>
public static class PlaceOrderEndpoint
{
    public static void MapPlaceOrder(this IEndpointRouteBuilder app) =>
        app.MapPost("/orders", async (PlaceOrder command, DocumentStore store, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var order = await PlaceOrderHandler.HandleAsync(store, http.GetScopeContext(), command, ct);
                return Results.Created($"/orders/{order.Id}", new { order.Id, order.Status, order.Total });
            }
            catch (DocumentNotFoundException)
            {
                return Results.NotFound(new { error = $"Unknown product {command.ProductId}." });
            }
            catch (OutOfStockException)
            {
                return Results.Conflict(new { error = "Out of stock." });
            }
        });
}
