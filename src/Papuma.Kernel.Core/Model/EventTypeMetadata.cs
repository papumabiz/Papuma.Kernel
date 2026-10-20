// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Model;

/// <summary>
/// The startup-built metadata of one registered event type (ADR-013): name, field
/// policies for the payload, and optional retention.
/// </summary>
/// <remarks>
/// Events are immutable facts — there is deliberately no upcaster chain (ADR-013):
/// transforming changes require a new event type, payload reading follows the
/// additive rules of ADR-005.
/// </remarks>
public sealed class EventTypeMetadata
{
    /// <summary>Gets the logical event type name (table column <c>event_type</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>
    /// Gets non-default field policies keyed by dot-separated JSON path.
    /// Paths not present default to <see cref="FieldPolicy.Track"/>.
    /// </summary>
    public IReadOnlyDictionary<string, FieldPolicy> Policies { get; }

    /// <summary>
    /// Gets the optional retention period — events older than this may be purged
    /// (ADR-013: the event log is a fact store, not a version store).
    /// </summary>
    public TimeSpan? Retention { get; }

    /// <summary>
    /// The policies as path segments, deepest path first, equal depths in ordinal path order. A policy on a path and
    /// one on a descendant (<c>address</c> hashed, <c>address.street</c> redacted) give one result in every
    /// declaration order: the descendant acts first, so the hash covers what remains (review C-3).
    /// </summary>
    internal IReadOnlyList<(string[] Segments, FieldPolicy Policy)> PoliciesDeepestFirst { get; }

    internal EventTypeMetadata(
        string name,
        Type clrType,
        IReadOnlyDictionary<string, FieldPolicy> policies,
        TimeSpan? retention)
    {
        Name = name;
        ClrType = clrType;
        Policies = policies;
        Retention = retention;
        PoliciesDeepestFirst =
        [
            .. policies
                .Select(p => (Path: p.Key, Segments: DiffPath.Split(p.Key), p.Value))
                .OrderByDescending(p => p.Segments.Length)
                .ThenBy(p => p.Path, StringComparer.Ordinal)
                .Select(p => (p.Segments, p.Value)),
        ];
    }
}
