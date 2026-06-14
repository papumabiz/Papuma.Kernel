// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// One entry of the change feed as delivered to change handlers (ADR-002/004/009).
/// </summary>
/// <param name="Seq">The global feed sequence number.</param>
/// <param name="Scope">The scope the change belongs to.</param>
/// <param name="DocumentType">The logical document type name.</param>
/// <param name="DocumentId">The document identifier.</param>
/// <param name="Version">The document version after the change.</param>
/// <param name="SchemaVersion">The schema version of the diff content (ADR-005).</param>
/// <param name="Operation">The change operation.</param>
/// <param name="Diff">The policy-applied reversible field diff (ADR-004/007).</param>
/// <param name="Metadata">Correlation/causation/actor and operation-specific metadata.</param>
/// <param name="OccurredAt">When the change was recorded.</param>
public sealed record ChangeRecord(
    long Seq,
    ScopeContext Scope,
    string DocumentType,
    string DocumentId,
    long Version,
    int SchemaVersion,
    ChangeOperation Operation,
    DocumentDiff Diff,
    JsonObject Metadata,
    DateTimeOffset OccurredAt)
{
    /// <summary>
    /// Returns whether the given dot-separated path changed in this record.
    /// </summary>
    /// <param name="path">The dot-separated JSON path (e.g. <c>address.city</c>).</param>
    public bool FieldChanged(string path) => Diff.Entries.ContainsKey(path);

    /// <summary>
    /// Returns whether this record is a field transition from one tracked value to
    /// another (e.g. <c>status: Pending → Paid</c>) — sugar for event translators (ADR-011).
    /// </summary>
    /// <param name="path">The dot-separated JSON path.</param>
    /// <param name="from">The expected old value (compared as JSON).</param>
    /// <param name="to">The expected new value (compared as JSON).</param>
    public bool IsFieldTransition(string path, JsonNode? from, JsonNode? to)
    {
        if (!Diff.Entries.TryGetValue(path, out var entry) || entry.Kind != DiffEntryKind.Tracked)
        {
            return false;
        }

        return JsonNode.DeepEquals(entry.Old, from) && JsonNode.DeepEquals(entry.New, to);
    }
}
