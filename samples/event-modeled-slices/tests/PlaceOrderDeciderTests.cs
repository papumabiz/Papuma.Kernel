// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using EventModeledSlices;
using EventModeledSlices.Features.PlaceOrder;

namespace EventModeledSlices.Tests;

/// <summary>
/// The command slice's business logic, tested with NO infrastructure — the core
/// promise of Event Modeling, kept on a document-sourced kernel
/// (event-modeling-slices.md, "Testing without infrastructure"). These are the pure
/// GIVEN/WHEN/THEN tests of the Decider: no database, no mocks, microseconds.
/// </summary>
public sealed class PlaceOrderDeciderTests
{
    private static readonly Product Grinder = new("p1", "Grinder", 200m);

    [Fact]
    public void GivenStock_WhenWithinStock_ThenApprovedOrder()
    {
        // GIVEN a product priced 200 with stock 5
        var stock = new Inventory("p1", 5);

        // WHEN placing an order for 1 (total 200, below the 500 threshold)
        var order = PlaceOrderDecider.Decide(Grinder, stock, new PlaceOrder("p1", 1));

        // THEN an approved order with the right total
        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.Equal(200m, order.Total);
        Assert.Equal("p1", order.ProductId);
        Assert.Equal(1, order.Quantity);
    }

    [Fact]
    public void GivenStock_WhenTotalOverThreshold_ThenPendingApproval()
    {
        // WHEN the total (200 × 3 = 600) exceeds the approval threshold
        var order = PlaceOrderDecider.Decide(Grinder, new Inventory("p1", 10), new PlaceOrder("p1", 3));

        // THEN the order needs approval
        Assert.Equal(OrderStatus.PendingApproval, order.Status);
        Assert.Equal(600m, order.Total);
    }

    [Fact]
    public void GivenInsufficientStock_WhenOrdering_ThenOutOfStock()
    {
        // GIVEN stock 1, WHEN ordering 2, THEN typed rejection
        var ex = Assert.Throws<OutOfStockException>(() =>
            PlaceOrderDecider.Decide(Grinder, new Inventory("p1", 1), new PlaceOrder("p1", 2)));
        Assert.Equal("p1", ex.ProductId);
    }

    [Fact]
    public void GivenNonPositiveQuantity_WhenOrdering_ThenRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaceOrderDecider.Decide(Grinder, new Inventory("p1", 5), new PlaceOrder("p1", 0)));
    }
}
