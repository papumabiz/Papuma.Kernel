// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Changes;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;

namespace EventModeledSlices.Features.OnOrderPlaced;

/// <summary>
/// An Automation slice (processor): reacts to the order's insert change and emits a
/// domain fact (<see cref="OrderPlaced"/>) into the event log. Invariant shape; the
/// trigger condition and the action are the variable parts.
///
/// Rules (slice-conventions, concepts §18/§19): handlers are idempotent
/// (at-least-once delivery) and never block; <see cref="Name"/> is the checkpoint
/// identity and must never be renamed.
/// </summary>
public sealed class OnOrderPlaced(DocumentStore store) : IChangeHandler
{
    public string Name => "on-order-placed";

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        // Trigger (variable): only a freshly inserted Order.
        if (change.DocumentType != nameof(Order) || change.Operation != ChangeOperation.Insert)
        {
            return;
        }

        await using var session = store.OpenSession(change.Scope);
        var order = await session.LoadAsync<Order>(change.DocumentId, ct);
        if (order is null)
        {
            return; // already gone — nothing to announce
        }

        // Action (variable): append the domain fact. AppendAsync is naturally
        // idempotent enough here for a demo; a production processor that issues a
        // state-changing command keys idempotency on a deterministic id (§18).
        await session.AppendAsync(
            new OrderPlaced(order.Document.Id, order.Document.ProductId, order.Document.Quantity), ct);
        await session.CommitAsync(ct);
    }
}
