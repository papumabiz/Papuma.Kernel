// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace EventModeledSlices.Features.PlaceOrder;

/// <summary>
/// The handler — invariant shape: load → decide → write, one session, one commit.
/// All the business judgement lives in <see cref="PlaceOrderDecider"/>; this stays
/// thin so the rule is unit-testable without a database.
/// </summary>
public static class PlaceOrderHandler
{
    public static async Task<Order> HandleAsync(
        DocumentStore store, ScopeContext scope, PlaceOrder command, CancellationToken ct)
    {
        await using var session = store.OpenSession(scope);

        var product = await session.LoadAsync<Product>(command.ProductId, ct)
            ?? throw new DocumentNotFoundException(nameof(Product), command.ProductId);
        var stock = await session.LoadAsync<Inventory>(command.ProductId, ct)
            ?? throw new DocumentNotFoundException(nameof(Inventory), command.ProductId);

        // Decide (pure). Then execute: the bounded counter (concepts §17) is the
        // race-safe second line of defense behind the decider's check.
        var order = PlaceOrderDecider.Decide(product.Document, stock.Document, command);

        await session.PatchAsync<Inventory>(command.ProductId,
            p => p.Increment(x => x.Stock, -command.Quantity), ct: ct);
        await session.SaveAsync(order, expectedVersion: 0, ct);
        await session.CommitAsync(ct);

        return order;
    }
}
