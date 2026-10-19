// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// What a policy needs to hash a value: the key provider (if one is configured), and the scope
/// and type the key is derived for (ADR-025). Without a provider the legacy v1 hash is produced
/// — a transitional state that ends when a configured key becomes mandatory.
/// </summary>
internal readonly record struct HashContext(IHashKeyProvider? Provider, ScopeContext Scope, string TypeName)
{
    /// <summary>Returns the hash of <paramref name="value"/>: v2 when a provider is configured, v1 otherwise.</summary>
    public string Hash(JsonNode? value) =>
        Provider is null
            ? PolicyHash.Of(value)
            : HashV2.Hash(Provider.GetKey(Scope, TypeName).Span, Provider.ActiveKeyId, value);
}
