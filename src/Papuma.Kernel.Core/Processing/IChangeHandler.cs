// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Processing;

/// <summary>
/// A change feed consumer (ADR-009): SQL projection, search index update, webhook,
/// audit log, event translator — the kernel does not care. The engine guarantees
/// per-handler delivery in causal order (per document strictly by version, ADR-022),
/// persisted checkpoints, retry with backoff, and at-least-once delivery:
/// implementations must be idempotent (natural idempotency key: handler name +
/// <see cref="ChangeRecord.Seq"/>). <c>Seq</c> identifies a change but is not a
/// watermark — a lower <c>seq</c> can arrive after a higher one.
/// </summary>
/// <remarks>
/// <para>
/// A handler that derives state also implements <see cref="IProjection"/>, so the kernel
/// can rebuild it; a handler with side effects (mail, webhook, bus) is an effect and is
/// never reset — mark it <see cref="StartsAtFeedHeadAttribute"/> so its first start does
/// not act on the whole history (ADR-024).
/// </para>
/// <para>
/// <b>Handlers see every scope.</b> The feed is read across all tenants and the platform scope
/// (<see cref="ChangeRecord.Scope"/> tells them apart), and no tenant context is set for the
/// call. A handler that reads or writes tenant data (a projection, a follow-up command) must
/// establish the record's scope itself, for example by opening its session with
/// <see cref="ChangeRecord.Scope"/>, and must not carry state from one record's tenant into the
/// next.
/// </para>
/// </remarks>
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
