// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The keyed hash of ADR-025: <c>"v2:" + kid + ":" + hex(HMAC-SHA-256(tenantKey, canonical(value)))</c>,
/// with a per-tenant, per-type key derived by HKDF from the application's master key.
/// Pure functions over bytes; key custody, the provider and the key-check table are the
/// caller's (ADR-025 points 3 to 5). Vectors: <c>docs/spec/vectors/hash.json</c>.
/// </summary>
internal static class HashV2
{
    /// <summary>The length in bytes of a derived key.</summary>
    public const int KeyLength = 32;

    /// <summary>The type name of the key used for the key-check value.</summary>
    public const string KeyCheckTypeName = "papuma:key-check";

    private static readonly byte[] HkdfSalt = Encoding.ASCII.GetBytes("papuma-hash-v2");

    private static readonly byte[] KeyCheckMessage = Encoding.ASCII.GetBytes("papuma-hash-check");

    /// <summary>
    /// Derives the key for one tenant and type from the master key. <paramref name="tenantId"/>
    /// is empty for platform scope; <paramref name="typeName"/> is the document type, or the
    /// event type for event payloads.
    /// </summary>
    public static byte[] DeriveTenantKey(ReadOnlySpan<byte> masterKey, string tenantId, string typeName)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var info = Encoding.UTF8.GetBytes($"tenant:{tenantId}\0type:{typeName}");
        var key = new byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, key, HkdfSalt, info);
        return key;
    }

    /// <summary>Returns the v2 hash string of <paramref name="value"/> under <paramref name="tenantKey"/>.</summary>
    public static string Hash(ReadOnlySpan<byte> tenantKey, string keyId, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        var mac = HMACSHA256.HashData(tenantKey, CanonicalJson.ToUtf8(value));
        return $"v2:{keyId}:{Convert.ToHexStringLower(mac)}";
    }

    /// <summary>
    /// The key-check value for a master key: the HMAC of a fixed message under the key of
    /// platform scope and <see cref="KeyCheckTypeName"/>. Equal master keys give equal values;
    /// the value reveals nothing usable about the key.
    /// </summary>
    public static string KeyCheck(ReadOnlySpan<byte> masterKey)
    {
        var key = DeriveTenantKey(masterKey, tenantId: string.Empty, KeyCheckTypeName);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, KeyCheckMessage));
    }
}
