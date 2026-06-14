// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;

using SessionOptions = Papuma.Kernel.Store.SessionOptions;

namespace ShopMinimalApi.Handlers;

/// <summary>
/// The saga / process manager of the sample (concepts §18, recipe: workflow-saga).
/// One dumb change handler drives all order-workflow transitions:
///
///   Order(PendingApproval) ──creates──► ApprovalTask(Pending)
///   ApprovalTask(Approved) ──patches──► Order(Approved)
///   ApprovalTask(Rejected) ──patches──► Order(Rejected) + compensates the stock
///
/// The rules this handler lives by (ADR-009, concepts §18/§19):
/// - it NEVER waits for a human — it materializes the question as a document,
/// - it is idempotent (at-least-once delivery: deterministic ids + state checks),
/// - on a ConcurrencyException it simply rethrows: the engine retries, and the
///   retry re-evaluates the NEW state — exactly the semantics a state machine wants.
/// </summary>
public sealed class OrderWorkflowHandler(DocumentStore store) : IChangeHandler
{
    public string Name => "order-workflow"; // checkpoint identity — never rename

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        switch (change.DocumentType)
        {
            case nameof(Order) when change.Operation == ChangeOperation.Insert:
                await OnOrderPlacedAsync(change, ct);
                break;

            case nameof(ApprovalTask) when change.FieldChanged("status"):
                await OnApprovalDecidedAsync(change, ct);
                break;
        }
    }

    /// <summary>Step 1: an order that needs approval materializes a task document.</summary>
    private async Task OnOrderPlacedAsync(ChangeRecord change, CancellationToken ct)
    {
        // The insert diff carries the full initial state — no extra load needed.
        if ((string?)change.Diff.Entries["status"].New != OrderStatus.PendingApproval)
        {
            return; // auto-approved orders skip the human entirely
        }

        await using var session = store.OpenSession(change.Scope,
            new SessionOptions { CausationId = $"change:{change.Seq}" });

        // Deterministic id: a redelivery of this change creates the SAME task —
        // the second save collides typed instead of duplicating (concepts §18).
        var task = new ApprovalTask(
            Id: $"approval-{change.DocumentId}",
            OrderId: change.DocumentId,
            Status: ApprovalStatus.Pending,
            RequestedAt: change.OccurredAt,
            DueAt: change.OccurredAt.AddMinutes(2)); // short for demo purposes

        try
        {
            await session.SaveAsync(task, expectedVersion: 0, ct);
            await session.CommitAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // At-least-once redelivery: the task already exists. Done.
        }
    }

    /// <summary>Step 2: the human decision (a normal write) drives the order transition.</summary>
    private async Task OnApprovalDecidedAsync(ChangeRecord change, CancellationToken ct)
    {
        // The decider travels inside the triggering diff — carrying it as the actor
        // makes the order's history answer "who approved this?" without joins.
        var decidedBy = change.Diff.Entries.TryGetValue("decidedBy", out var entry)
            ? (string?)entry.New
            : null;

        await using var session = store.OpenSession(change.Scope,
            new SessionOptions { ActorId = decidedBy, CausationId = $"change:{change.Seq}" });

        var task = await session.LoadAsync<ApprovalTask>(change.DocumentId, ct);
        if (task is null || task.Document.Status == ApprovalStatus.Pending)
        {
            return;
        }

        var order = await session.LoadAsync<Order>(task.Document.OrderId, ct);
        if (order is null || order.Document.Status != OrderStatus.PendingApproval)
        {
            return; // transition already happened (idempotency under at-least-once)
        }

        switch (task.Document.Status)
        {
            case ApprovalStatus.Approved:
                await session.SaveAsync(
                    order.Document with { Status = OrderStatus.Approved }, order.Version, ct);
                break;

            case ApprovalStatus.Rejected:
                // Saga compensation: the rejected order gives its stock back.
                // Increment(+quantity) is the sanctioned counter write (concepts §17);
                // order transition + compensation commit atomically in this session.
                await session.SaveAsync(
                    order.Document with { Status = OrderStatus.Rejected }, order.Version, ct);
                await session.PatchAsync<Inventory>(order.Document.ProductId,
                    p => p.Increment(x => x.Stock, order.Document.Quantity), ct: ct);
                break;

            // ApprovalStatus.Escalated is a nudge, not a decision — the order stays
            // PendingApproval and the task remains decidable (see the escalation poller).
        }

        // A ConcurrencyException here (parallel writer on the order) bubbles up on
        // purpose: the engine retries with backoff, and the retry sees the new state.
        await session.CommitAsync(ct);
    }
}
