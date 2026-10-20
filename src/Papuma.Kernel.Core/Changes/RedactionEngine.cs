// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// GDPR redaction primitives (ADR-015) — pure JSON manipulation, storage-neutral.
/// Extracted from the Postgres kernel's <c>DocumentSession.Gdpr.cs</c> so any storage
/// backend's redaction path (row selection, locking strategy, persistence) can share the
/// same decision logic and audit-block shape.
/// </summary>
internal static class RedactionEngine
{
    /// <summary>
    /// Rewrites matching diff entries (wire format, ADR-004) to the redaction marker
    /// <c>{"changed": true}</c>. Returns whether anything changed — already-redacted
    /// entries make the operation idempotent.
    /// </summary>
    public static bool RedactDiffEntries(JsonObject diff, IReadOnlyCollection<string>? paths)
    {
        var changed = false;
        foreach (var entryPath in diff.Select(e => e.Key).ToList())
        {
            // An entry is erased when an erasure path covers it, or when it is an ancestor that carries the
            // erased field inside its value — a whole-object entry written before policies were split
            // (insert, delete, an object set or cleared) holds address.street under "address".
            if (paths is not null
                && !paths.Any(p => PathCovers(p, entryPath) || CarriesInValue(diff[entryPath], entryPath, p)))
            {
                continue;
            }

            // Idempotency: a pure {"changed": true} marker is already fully redacted.
            if (diff[entryPath] is JsonObject { Count: 1 } existing && existing.ContainsKey("changed"))
            {
                continue;
            }

            diff[entryPath] = new JsonObject { ["changed"] = true };
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Removes payload fields at the given dot-separated paths. Returns whether anything
    /// was actually removed (missing paths are a no-op, not an error — idempotent).
    /// </summary>
    public static bool RemovePayloadPaths(JsonObject payload, IReadOnlyCollection<string> paths)
    {
        var changed = false;
        foreach (var path in paths)
        {
            var segments = DiffPath.Split(path);
            var current = payload;
            for (var i = 0; i < segments.Length - 1 && current is not null; i++)
            {
                current = current[segments[i]] as JsonObject;
            }

            if (current is not null && current.Remove(segments[^1]))
            {
                changed = true;
            }
        }

        return changed;
    }

    // Whether the entry at entryPath is a strict ancestor of erasedPath and its old or new value holds the field.
    private static bool CarriesInValue(JsonNode? entry, string entryPath, string erasedPath)
    {
        if (entryPath == erasedPath || !PathCovers(entryPath, erasedPath) || entry is not JsonObject entryJson)
        {
            return false;
        }

        var below = DiffPath.Split(erasedPath[(entryPath.Length + 1)..]);
        return Holds(entryJson["old"], below) || Holds(entryJson["new"], below);
    }

    private static bool Holds(JsonNode? value, string[] segments)
    {
        foreach (var segment in segments)
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether <paramref name="declared"/> covers <paramref name="path"/> (self or descendant).</summary>
    public static bool PathCovers(string declared, string path) =>
        path == declared || (path.Length > declared.Length && path[declared.Length] == '.'
            && path.StartsWith(declared, StringComparison.Ordinal));

    /// <summary>
    /// Builds the mandatory audit block merged into the metadata of every rewritten
    /// record: when, why, who (when the caller carries an actor), correlation.
    /// </summary>
    /// <param name="correlationId">The session's correlation id.</param>
    /// <param name="actorId">The session's actor id, when known.</param>
    /// <param name="reason">The mandatory audit reason (e.g. the erasure request reference).</param>
    /// <param name="paths">The redacted paths, or <c>null</c> for "all".</param>
    public static JsonObject BuildRedactionAudit(
        Guid correlationId, string? actorId, string reason, IReadOnlyCollection<string>? paths)
    {
        var redaction = new JsonObject
        {
            ["redactedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["reason"] = reason,
            ["correlationId"] = correlationId.ToString("N"),
        };

        if (actorId is not null)
        {
            redaction["actorId"] = actorId;
        }

        if (paths is null)
        {
            redaction["paths"] = "all";
        }
        else
        {
            var array = new JsonArray();
            foreach (var path in paths)
            {
                array.Add(path);
            }

            redaction["paths"] = array;
        }

        return new JsonObject { ["redaction"] = redaction };
    }
}
