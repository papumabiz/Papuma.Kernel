// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Processing;

/// <summary>
/// Declares a feed handler as a projection (ADR-024): it derives state from the feed,
/// so replaying it is safe — a new one backfills from the beginning, and the kernel may
/// rebuild it. Implement it next to <see cref="IChangeHandler"/> or
/// <c>IEventHandler</c>. Handlers without it are effects (mails, webhooks, bridges),
/// which the kernel never resets on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versioned rebuild.</b> The checkpoint stores <see cref="Version"/>. When a start
/// finds a lower stored version, the processor calls <see cref="ResetAsync"/> and
/// replays the feed from the beginning, once — a breaking projection change is "bump
/// the version", under the same handler name. An instance that finds a <em>higher</em>
/// stored version runs older code (a rolling deploy) and pauses the projection instead
/// of writing the old shape into the rebuilt target.
/// </para>
/// <para>
/// The first start with a stored checkpoint but no stored version records the version
/// without a rebuild. To rebuild after an upgrade, bump the version or call
/// <c>ResetProjectionsAsync</c> on the processor.
/// </para>
/// </remarks>
public interface IProjection
{
    /// <summary>
    /// Gets the version of the projection's output shape. Increase it for a change that
    /// needs a rebuild (a new column filled from history, a different key, a different
    /// meaning); leave it for changes that apply to new deliveries only. Not negative.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Empties this projection's own target — its table, index or file — before a
    /// rebuild replays the feed into it. Must be idempotent (<c>DELETE</c>,
    /// <c>TRUNCATE</c>): after a crash between the reset and the recorded rebuild, the
    /// next start calls it again.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    Task ResetAsync(CancellationToken ct);
}

/// <summary>
/// Starts a handler at the head of its feed when it is registered for the first time
/// (ADR-024): everything committed before counts as delivered. For effects — a mail or
/// webhook handler added to a system with history must not act on that history. It has
/// no effect on a handler that already has a checkpoint, and it cannot be combined with
/// <see cref="IProjection"/> (a projection that skips history is incomplete).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class StartsAtFeedHeadAttribute : Attribute;

/// <summary>What the processors need to know about a handler's kind (ADR-024).</summary>
internal static class HandlerKind
{
    /// <summary>
    /// Returns the declared projection version, or <c>null</c> for an effect.
    /// </summary>
    /// <exception cref="ArgumentException">The declaration is contradictory or invalid.</exception>
    public static int? Validate(object handler, string handlerName)
    {
        var startsAtHead = Attribute.IsDefined(handler.GetType(), typeof(StartsAtFeedHeadAttribute));
        if (handler is not IProjection projection)
        {
            return null;
        }

        if (startsAtHead)
        {
            throw new ArgumentException(
                $"Handler {handlerName} is a projection and cannot start at the feed head: a projection that skips history is incomplete.",
                nameof(handler));
        }

        if (projection.Version < 0)
        {
            throw new ArgumentException($"Projection {handlerName} declares a negative version.", nameof(handler));
        }

        return projection.Version;
    }

    /// <summary>Whether the handler's first registration starts at the feed head.</summary>
    public static bool StartsAtHead(object handler) =>
        Attribute.IsDefined(handler.GetType(), typeof(StartsAtFeedHeadAttribute));
}
