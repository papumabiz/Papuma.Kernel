// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// Applies field policies (ADR-007/013) to an event payload before it is stored.
/// Unlike diff entries, event payloads keep their natural shape: Redact/DoNotTrack
/// remove the field, Hash replaces the value with its hash string (ADR-025).
/// </summary>
internal static class EventPayloadPolicyApplier
{
    public static JsonObject Apply(
        JsonObject payload,
        EventTypeMetadata metadata,
        IHashKeyProvider? hashKeys,
        ScopeContext scope)
    {
        if (metadata.Policies.Count == 0)
        {
            return payload;
        }

        var hash = new HashContext(hashKeys, scope, metadata.Name);
        var result = payload.DeepClone().AsObject();
        foreach (var (segments, policy) in metadata.PoliciesDeepestFirst)
        {
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
                    parent[field] = hash.Hash(parent[field]);
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
