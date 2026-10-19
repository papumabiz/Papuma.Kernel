// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Cryptography;
using System.Text;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The key-check value of ADR-025 point 5 for a provider, and the comparison against the value a
/// database stored for the same key id. Engine-neutral: the PostgreSQL and SQLite schema managers
/// only persist the value (<c>hash_key_check</c>) and call <see cref="Verify"/>.
/// </summary>
internal static class HashKeyCheckValue
{
    /// <summary>
    /// Computes the check value: the HMAC of a fixed message under the provider's key for platform
    /// scope and <see cref="HashV2.KeyCheckTypeName"/>. For the default provider this equals
    /// <see cref="HashV2.KeyCheck(ReadOnlySpan{byte})"/> of its master key.
    /// </summary>
    public static string Compute(IHashKeyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        MasterKeyHashKeyProvider.ValidateKeyId(provider.ActiveKeyId);
        var key = provider.GetKey(ScopeContext.Platform(), HashV2.KeyCheckTypeName);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key.Span, HashV2.KeyCheckMessageBytes));
    }

    /// <summary>Throws <see cref="HashKeyMismatchException"/> when the stored value differs from the computed one.</summary>
    public static void Verify(string keyId, string computed, string stored)
    {
        var equal = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(stored));
        if (!equal)
        {
            throw new HashKeyMismatchException(keyId);
        }
    }
}
