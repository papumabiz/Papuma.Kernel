// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// One field-level entry of a reversible document diff (ADR-004).
/// </summary>
/// <remarks>
/// Presence and value are distinct: <see cref="HasOld"/> / <see cref="HasNew"/> indicate
/// whether the field existed at all, while <see cref="Old"/> / <see cref="New"/> may
/// legitimately be JSON <c>null</c>. This distinction keeps the diff reversible —
/// "field added" (<c>HasOld == false</c>) is not the same as "field changed from null".
/// </remarks>
public sealed class DiffEntry
{
    /// <summary>Gets a value indicating whether the field existed before the change.</summary>
    public bool HasOld { get; }

    /// <summary>Gets the previous value (may be JSON null). Only meaningful when <see cref="HasOld"/> is true.</summary>
    public JsonNode? Old { get; }

    /// <summary>Gets a value indicating whether the field exists after the change.</summary>
    public bool HasNew { get; }

    /// <summary>Gets the new value (may be JSON null). Only meaningful when <see cref="HasNew"/> is true.</summary>
    public JsonNode? New { get; }

    private DiffEntry(bool hasOld, JsonNode? old, bool hasNew, JsonNode? @new)
    {
        HasOld = hasOld;
        Old = old;
        HasNew = hasNew;
        New = @new;
    }

    /// <summary>Creates an entry for a field whose value changed.</summary>
    public static DiffEntry Changed(JsonNode? old, JsonNode? @new) => new(true, old, true, @new);

    /// <summary>Creates an entry for a field that did not exist before.</summary>
    public static DiffEntry Added(JsonNode? @new) => new(false, null, true, @new);

    /// <summary>Creates an entry for a field that no longer exists.</summary>
    public static DiffEntry Removed(JsonNode? old) => new(true, old, false, null);
}
