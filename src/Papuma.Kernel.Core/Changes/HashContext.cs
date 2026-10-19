// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// What a policy needs to hash a value: the key provider, and the scope and type the key is derived
/// for (ADR-025). There is no unkeyed hash: the kernel never writes a hash without a key, and the
/// stores refuse to be built for a model with a <c>Hash</c> policy and no provider. The check here is
/// the last line of defence for callers that reach a policy applier some other way.
/// </summary>
internal readonly record struct HashContext(IHashKeyProvider? Provider, ScopeContext Scope, string TypeName)
{
    /// <summary>Returns the v2 hash of <paramref name="value"/> under the key for this scope and type.</summary>
    /// <exception cref="InvalidOperationException">No key provider is configured.</exception>
    public string Hash(JsonNode? value) =>
        Provider is null
            ? throw new InvalidOperationException(
                $"A Hash policy applies to {TypeName}, but no IHashKeyProvider is configured (ADR-025).")
            : HashV2.Hash(Provider.GetKey(Scope, TypeName).Span, Provider.ActiveKeyId, value);
}
