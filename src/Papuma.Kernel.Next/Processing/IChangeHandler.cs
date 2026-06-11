// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Processing;

/// <summary>
/// A change feed consumer (ADR-009): SQL projection, search index update, webhook,
/// audit log, event translator — the kernel does not care. The engine guarantees
/// per-handler ordering by <c>seq</c>, persisted checkpoints, retry with backoff,
/// and at-least-once delivery: implementations must be idempotent
/// (natural idempotency key: handler name + <see cref="ChangeRecord.Seq"/>).
/// </summary>
public interface IChangeHandler
{
    /// <summary>Gets the unique, stable handler name (checkpoint key).</summary>
    string Name { get; }

    /// <summary>
    /// Applies a single change. Throw to trigger retry with backoff; after the
    /// configured maximum attempts the change is skipped (poison) and stays recorded
    /// in the failure table.
    /// </summary>
    /// <param name="change">The change record.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleAsync(ChangeRecord change, CancellationToken ct);
}
