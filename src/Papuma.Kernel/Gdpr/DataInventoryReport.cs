// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// The Art.-30 data inventory built from the metamodel (ADR-015): which document and
/// event types exist, which fields carry which policies, which event types are purged
/// when. Doubles as a review tool — <c>UnprotectedPaths</c> answers "which fields would
/// appear verbatim in the change feed?".
/// </summary>
/// <param name="Documents">The inventory of all registered document types.</param>
/// <param name="Events">The inventory of all registered event types.</param>
public sealed record DataInventoryReport(
    IReadOnlyList<DocumentInventory> Documents,
    IReadOnlyList<EventInventory> Events)
{
    /// <summary>
    /// Serializes the report to JSON (e.g. for an Art.-30 record attachment or a
    /// policy-review diff in CI).
    /// </summary>
    public JsonObject ToJson()
    {
        var documents = new JsonArray();
        foreach (var doc in Documents)
        {
            documents.Add(new JsonObject
            {
                ["documentType"] = doc.Name,
                ["clrType"] = doc.ClrType,
                ["schemaVersion"] = doc.SchemaVersion,
                ["fields"] = FieldsToJson(doc.Fields),
                ["unprotectedPaths"] = ToArray(doc.UnprotectedPaths),
                ["uniqueKeys"] = ToArray(doc.UniqueKeys),
                ["lookupKeys"] = ToArray(doc.LookupKeys),
            });
        }

        var events = new JsonArray();
        foreach (var evt in Events)
        {
            events.Add(new JsonObject
            {
                ["eventType"] = evt.Name,
                ["clrType"] = evt.ClrType,
                ["retention"] = evt.Retention?.ToString(),
                ["fields"] = FieldsToJson(evt.Fields),
                ["unprotectedPaths"] = ToArray(evt.UnprotectedPaths),
            });
        }

        return new JsonObject
        {
            ["documents"] = documents,
            ["events"] = events,
        };
    }

    private static JsonArray FieldsToJson(IReadOnlyList<FieldInventory> fields)
    {
        var json = new JsonArray();
        foreach (var field in fields)
        {
            json.Add(new JsonObject
            {
                ["path"] = field.Path,
                ["policy"] = field.Policy.ToString(),
            });
        }

        return json;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var json = new JsonArray();
        foreach (var value in values)
        {
            json.Add(value);
        }

        return json;
    }
}

/// <summary>
/// The inventory entry of one document type.
/// </summary>
/// <param name="Name">The logical document type name.</param>
/// <param name="ClrType">The full CLR type name.</param>
/// <param name="SchemaVersion">The current schema version (ADR-005).</param>
/// <param name="Fields">All leaf field paths with their effective policy.</param>
/// <param name="UniqueKeys">The declared unique key paths (ADR-006).</param>
/// <param name="LookupKeys">The declared lookup key paths (ADR-006).</param>
public sealed record DocumentInventory(
    string Name,
    string ClrType,
    int SchemaVersion,
    IReadOnlyList<FieldInventory> Fields,
    IReadOnlyList<string> UniqueKeys,
    IReadOnlyList<string> LookupKeys)
{
    /// <summary>
    /// Gets the paths whose values appear verbatim in change diffs
    /// (effective policy <see cref="FieldPolicy.Track"/>) — the review list for
    /// "is any of these personal data?" (ADR-015).
    /// </summary>
    public IReadOnlyList<string> UnprotectedPaths =>
        Fields.Where(f => f.Policy == FieldPolicy.Track).Select(f => f.Path).ToList();
}

/// <summary>
/// The inventory entry of one event type.
/// </summary>
/// <param name="Name">The logical event type name.</param>
/// <param name="ClrType">The full CLR type name.</param>
/// <param name="Fields">All leaf payload paths with their effective policy.</param>
/// <param name="Retention">The opt-in retention after which events are purged (ADR-013).</param>
public sealed record EventInventory(
    string Name,
    string ClrType,
    IReadOnlyList<FieldInventory> Fields,
    TimeSpan? Retention)
{
    /// <summary>
    /// Gets the payload paths stored verbatim (effective policy <see cref="FieldPolicy.Track"/>).
    /// </summary>
    public IReadOnlyList<string> UnprotectedPaths =>
        Fields.Where(f => f.Policy == FieldPolicy.Track).Select(f => f.Path).ToList();
}

/// <summary>
/// One leaf field path with its effective policy (attribute defaults, fluent overrides
/// and ancestor inheritance resolved).
/// </summary>
/// <param name="Path">The dot-separated JSON path.</param>
/// <param name="Policy">The effective field policy.</param>
public sealed record FieldInventory(string Path, FieldPolicy Policy);
