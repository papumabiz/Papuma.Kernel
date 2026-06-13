// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace EventModeledSlices.Features.PlaceOrder;

/// <summary>
/// The business rule — a pure function, no I/O (slice-conventions: the one VARIABLE
/// file). This is the Decider's <c>decide</c> half; in a document-sourced kernel the
/// <c>evolve</c> half collapses (the store folds), so the decision produces the new
/// state directly. Fully unit-testable without infrastructure.
/// </summary>
public static class PlaceOrderDecider
{
    public const decimal ApprovalThreshold = 500m;

    public static Order Decide(Product product, Inventory stock, PlaceOrder command)
    {
        if (command.Quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Quantity must be positive.");
        }

        if (stock.Stock < command.Quantity)
        {
            throw new OutOfStockException(product.Id);
        }

        var total = product.Price * command.Quantity;
        return new Order(
            Id: Guid.NewGuid().ToString("N"),
            ProductId: product.Id,
            Quantity: command.Quantity,
            Total: total,
            Status: total > ApprovalThreshold ? OrderStatus.PendingApproval : OrderStatus.Approved);
    }
}
