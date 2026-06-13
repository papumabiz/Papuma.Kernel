// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Applies field policies to a whole document on read (ADR-016, concepts §24) — the
/// read-time counterpart of <see cref="PolicyApplier"/>. The invariant: a
/// policy-projected document shows exactly what the feed shows, never more. Only
/// <see cref="FieldPolicy.Track"/> fields keep their clear-text value.
/// </summary>
internal static class PolicyProjector
{
    /// <summary>The placeholder substituted for redacted/referenced field values.</summary>
    public const string ProtectedMarker = "[protected]";

    /// <summary>
    /// Returns a masked clone of <paramref name="document"/> with field policies applied.
    /// The input is not mutated. Documents without any non-default policy are returned
    /// as a clone unchanged.
    /// </summary>
    /// <param name="document">The raw (upcast) document.</param>
    /// <param name="metadata">The document type metadata carrying the policies.</param>
    public static JsonObject Apply(JsonObject document, DocumentTypeMetadata metadata)
    {
        var clone = (JsonObject)document.DeepClone();
        if (metadata.Policies.Count == 0)
        {
            return clone;
        }

        MaskObject(clone, prefix: string.Empty, metadata);
        return clone;
    }

    private static void MaskObject(JsonObject obj, string prefix, DocumentTypeMetadata metadata)
    {
        foreach (var key in obj.Select(p => p.Key).ToList())
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            switch (metadata.ResolvePolicy(path))
            {
                case FieldPolicy.Track:
                    // Recurse into nested objects; arrays and scalars stay verbatim
                    // (a policy on the array/object path itself is handled below).
                    if (obj[key] is JsonObject nested)
                    {
                        MaskObject(nested, path, metadata);
                    }

                    break;

                case FieldPolicy.DoNotTrack:
                    obj.Remove(key); // never in the feed → never in a projected read
                    break;

                case FieldPolicy.Hash:
                    obj[key] = PolicyHash.Of(obj[key]); // same hash the feed would show
                    break;

                case FieldPolicy.Redact:
                case FieldPolicy.Reference:
                    // Keep the key (the agent sees the field exists) but never its value.
                    obj[key] = ProtectedMarker;
                    break;
            }
        }
    }
}
