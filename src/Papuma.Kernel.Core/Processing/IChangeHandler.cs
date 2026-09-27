// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Processing;

/// <summary>
/// A change feed consumer (ADR-009): SQL projection, search index update, webhook,
/// audit log, event translator — the kernel does not care. The engine guarantees
/// per-handler delivery in commit order (per document strictly by version, ADR-022),
/// persisted checkpoints, retry with backoff, and at-least-once delivery:
/// implementations must be idempotent (natural idempotency key: handler name +
/// <see cref="ChangeRecord.Seq"/>). <c>Seq</c> identifies a change but is not a
/// watermark — a lower <c>seq</c> can arrive after a higher one.
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
