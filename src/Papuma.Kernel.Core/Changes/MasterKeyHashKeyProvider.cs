// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The default <see cref="IHashKeyProvider"/>: derives the key for each tenant and type from one
/// application master key by HKDF-SHA-256 (ADR-025, <c>docs/spec/canonical-json.md</c> §2.1). The
/// same value therefore hashes differently in another tenant and in another type, without a key
/// being stored per tenant. Every process that writes must be configured with the same master key
/// and key id.
/// </summary>
public sealed class MasterKeyHashKeyProvider : IHashKeyProvider
{
    /// <summary>The shortest master key accepted, in bytes.</summary>
    public const int MinMasterKeyLength = 32;

    /// <summary>The longest key id accepted, in characters.</summary>
    public const int MaxKeyIdLength = 32;

    // Derivation is one HKDF; the cache only has to stay bounded.
    private const int MaxCachedKeys = 4096;

    private readonly byte[] _masterKey;
    private readonly ConcurrentDictionary<(string TenantId, string TypeName), byte[]> _cache = new();

    /// <inheritdoc />
    public string ActiveKeyId { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MasterKeyHashKeyProvider"/> class.
    /// </summary>
    /// <param name="masterKey">The application master key: at least <see cref="MinMasterKeyLength"/> random bytes. Copied.</param>
    /// <param name="keyId">The identifier of this master key, written into every hash.</param>
    public MasterKeyHashKeyProvider(ReadOnlySpan<byte> masterKey, string keyId)
    {
        if (masterKey.Length < MinMasterKeyLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(masterKey), masterKey.Length, $"The hash master key must be at least {MinMasterKeyLength} bytes.");
        }

        ValidateKeyId(keyId);
        _masterKey = masterKey.ToArray();
        ActiveKeyId = keyId;
    }

    /// <summary>Creates a provider from a base64 master key — the form it takes in configuration.</summary>
    /// <param name="masterKeyBase64">The master key, base64-encoded.</param>
    /// <param name="keyId">The identifier of this master key.</param>
    public static MasterKeyHashKeyProvider FromBase64(string masterKeyBase64, string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKeyBase64);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(masterKeyBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The hash master key is not valid base64.", nameof(masterKeyBase64), ex);
        }

        return new MasterKeyHashKeyProvider(bytes, keyId);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> GetKey(ScopeContext scope, string typeName)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        var cacheKey = (scope.TenantId ?? string.Empty, typeName);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var key = HashV2.DeriveTenantKey(_masterKey, cacheKey.Item1, typeName);
        if (_cache.Count >= MaxCachedKeys)
        {
            _cache.Clear();
        }

        _cache[cacheKey] = key;
        return key;
    }

    internal static void ValidateKeyId(string keyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        if (keyId.Length > MaxKeyIdLength
            || !keyId.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
        {
            throw new ArgumentException(
                $"The hash key id must be 1 to {MaxKeyIdLength} characters of A-Z a-z 0-9 _ -.", nameof(keyId));
        }
    }
}
