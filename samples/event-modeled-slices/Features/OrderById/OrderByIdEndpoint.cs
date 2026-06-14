// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Store;

namespace EventModeledSlices.Features.OrderById;

/// <summary>
/// A View slice — the document-sourced twist (concepts §16): the current state of one
/// document is a direct <c>LoadAsync</c>, not a materialized projection. Strongly
/// consistent, no lag, no read-model to maintain. (Reserve projections for
/// aggregations / search / external targets.)
/// </summary>
public static class OrderByIdEndpoint
{
    public static void MapOrderById(this IEndpointRouteBuilder app) =>
        app.MapGet("/orders/{id}", async (string id, DocumentStore store, HttpContext http, CancellationToken ct) =>
        {
            await using var session = store.OpenSession(http.GetScopeContext());
            var order = await session.LoadAsync<Order>(id, ct);
            return order is null
                ? Results.NotFound()
                : Results.Ok(new { order.Document, order.Version });
        });
}
