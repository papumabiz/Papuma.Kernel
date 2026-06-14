// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The shape of one diff entry after policy application (ADR-004/007).
/// </summary>
public enum DiffEntryKind
{
    /// <summary>Old/new values recorded verbatim.</summary>
    Tracked = 0,

    /// <summary>Only the fact of the change recorded.</summary>
    Redacted = 1,

    /// <summary>A reference to the value's location recorded instead of the value.</summary>
    Reference = 2,

    /// <summary>Change marker plus SHA-256 hash of the new value recorded.</summary>
    Hashed = 3,
}

/// <summary>
/// One field-level entry of a reversible document diff (ADR-004).
/// </summary>
/// <remarks>
/// For <see cref="DiffEntryKind.Tracked"/> entries, presence and value are distinct:
/// <see cref="HasOld"/> / <see cref="HasNew"/> indicate whether the field existed at
/// all, while <see cref="Old"/> / <see cref="New"/> may legitimately be JSON <c>null</c>.
/// This distinction keeps the diff reversible — "field added" is not the same as
/// "field changed from null". Non-tracked entries carry no values and are therefore
/// not reversible (ADR-007/008).
/// </remarks>
public sealed class DiffEntry
{
    /// <summary>Gets the policy-applied shape of this entry.</summary>
    public DiffEntryKind Kind { get; }

    /// <summary>Gets a value indicating whether the field existed before the change (tracked entries only).</summary>
    public bool HasOld { get; }

    /// <summary>Gets the previous value (may be JSON null). Only meaningful when <see cref="HasOld"/> is true.</summary>
    public JsonNode? Old { get; }

    /// <summary>Gets a value indicating whether the field exists after the change (tracked entries only).</summary>
    public bool HasNew { get; }

    /// <summary>Gets the new value (may be JSON null). Only meaningful when <see cref="HasNew"/> is true.</summary>
    public JsonNode? New { get; }

    /// <summary>Gets the value reference (<see cref="DiffEntryKind.Reference"/> entries only).</summary>
    public string? Reference { get; }

    /// <summary>Gets the SHA-256 hex hash of the new value (<see cref="DiffEntryKind.Hashed"/> entries; null when the field was removed).</summary>
    public string? Hash { get; }

    private DiffEntry(
        DiffEntryKind kind,
        bool hasOld,
        JsonNode? old,
        bool hasNew,
        JsonNode? @new,
        string? reference,
        string? hash)
    {
        Kind = kind;
        HasOld = hasOld;
        Old = old;
        HasNew = hasNew;
        New = @new;
        Reference = reference;
        Hash = hash;
    }

    /// <summary>Creates a tracked entry for a field whose value changed.</summary>
    public static DiffEntry Changed(JsonNode? old, JsonNode? @new) =>
        new(DiffEntryKind.Tracked, true, old, true, @new, null, null);

    /// <summary>Creates a tracked entry for a field that did not exist before.</summary>
    public static DiffEntry Added(JsonNode? @new) =>
        new(DiffEntryKind.Tracked, false, null, true, @new, null, null);

    /// <summary>Creates a tracked entry for a field that no longer exists.</summary>
    public static DiffEntry Removed(JsonNode? old) =>
        new(DiffEntryKind.Tracked, true, old, false, null, null, null);

    /// <summary>Creates a redacted entry — the field changed, values are withheld (ADR-007).</summary>
    public static DiffEntry RedactedEntry() =>
        new(DiffEntryKind.Redacted, false, null, false, null, null, null);

    /// <summary>Creates a reference entry pointing at the value's location (ADR-007).</summary>
    public static DiffEntry ReferenceEntry(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new(DiffEntryKind.Reference, false, null, false, null, reference, null);
    }

    /// <summary>Creates a hashed entry — change marker plus hash of the new value (ADR-007).</summary>
    public static DiffEntry HashedEntry(string? hash) =>
        new(DiffEntryKind.Hashed, false, null, false, null, null, hash);
}
