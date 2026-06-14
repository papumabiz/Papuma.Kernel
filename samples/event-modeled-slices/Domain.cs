// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace EventModeledSlices;

// A deliberately tiny domain — just enough to show one slice of each type.
// Compare the full Minimal-API sample (shop-minimal-api) for the breadth of
// features; this sample shows the *shape* of event-modeled vertical slices.

public sealed record Product(string Id, string Name, decimal Price);

public sealed record Inventory(string Id, int Stock);

public static class OrderStatus
{
    public const string Approved = "Approved";
    public const string PendingApproval = "PendingApproval";
}

public sealed record Order(
    string Id,
    string ProductId,
    int Quantity,
    decimal Total,
    string Status);

/// <summary>A domain fact emitted by the OnOrderPlaced automation (event log, ADR-013).</summary>
public sealed record OrderPlaced(string OrderId, string ProductId, int Quantity);

/// <summary>Typed rejection from the bounded counter (concepts §17).</summary>
public sealed class OutOfStockException(string productId)
    : Exception($"Product '{productId}' is out of stock.")
{
    public string ProductId { get; } = productId;
}
