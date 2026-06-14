// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;

namespace ShopMinimalApi;

// The domain of the sample shop. Plain records — the kernel needs nothing more
// than a string Id (convention) and optional policy attributes (ADR-007).
// Statuses are string constants instead of enums so that diffs and SQL reads
// (the escalation poller) stay human-readable.

/// <summary>A catalog product. Content changes are independent of stock movements.</summary>
public sealed record Product(string Id, string Name, decimal Price);

/// <summary>
/// Stock as its own small document per product (concepts §17): decoupling content
/// maintenance from stock movements keeps both histories clean, and the change feed
/// of this document is a gapless stock ledger.
/// </summary>
public sealed record Inventory(string Id, int Stock);

public static class OrderStatus
{
    public const string PendingApproval = "PendingApproval"; // waiting for a human (concepts §18)
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Shipped = "Shipped";
}

/// <summary>
/// The workflow instance (concepts §18): a durable state machine whose transitions
/// are protected by <c>expectedVersion</c> and whose change feed is the complete,
/// auditable execution log.
/// </summary>
public sealed record Order(
    string Id,
    string ProductId,
    int Quantity,
    decimal Total,
    string Status,
    [property: SensitiveData] string? CustomerEmail = null); // policy: never in the feed

public static class ApprovalStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Escalated = "Escalated";
}

/// <summary>
/// The human-in-the-loop task (concepts §18): the handler materializes the question
/// as a document and returns — waiting is state in the store, not a blocked thread.
/// The human decision is a normal patch with <c>expectedVersion</c>.
/// </summary>
public sealed record ApprovalTask(
    string Id,
    string OrderId,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueAt,           // the timer field for the escalation poller
    string? DecidedBy = null);

/// <summary>
/// A fact without state truth (ADR-013): the delivery occurred — the resulting
/// stock level lives in the <see cref="Inventory"/> document, the occurrence
/// itself is appended to the event log.
/// </summary>
public sealed record StockReplenished(string ProductId, int Quantity);

/// <summary>Typed rejection of the bounded counter (concepts §17).</summary>
public sealed class OutOfStockException(string productId)
    : Exception($"Product '{productId}' is out of stock.")
{
    public string ProductId { get; } = productId;
}
