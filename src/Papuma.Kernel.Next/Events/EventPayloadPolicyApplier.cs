// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Events;

/// <summary>
/// Applies field policies (ADR-007/013) to an event payload before it is stored.
/// Unlike diff entries, event payloads keep their natural shape: Redact/DoNotTrack
/// remove the field, Hash replaces the value with a SHA-256 hex string.
/// </summary>
internal static class EventPayloadPolicyApplier
{
    public static JsonObject Apply(JsonObject payload, EventTypeMetadata metadata)
    {
        if (metadata.Policies.Count == 0)
        {
            return payload;
        }

        var result = payload.DeepClone().AsObject();
        foreach (var (path, policy) in metadata.Policies)
        {
            var segments = path.Split('.');
            var parent = NavigateToParent(result, segments);
            var field = segments[^1];
            if (parent is null || !parent.ContainsKey(field))
            {
                continue;
            }

            switch (policy)
            {
                case FieldPolicy.Redact:
                case FieldPolicy.DoNotTrack:
                    parent.Remove(field);
                    break;

                case FieldPolicy.Hash:
                    var text = parent[field]?.ToJsonString() ?? "null";
                    parent[field] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                    break;

                // Reference is rejected at model build time for events (ADR-013).
            }
        }

        return result;
    }

    private static JsonObject? NavigateToParent(JsonObject root, string[] segments)
    {
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                return null; // path not present in this payload
            }

            current = next;
        }

        return current;
    }
}
