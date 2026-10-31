// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Testing;

/// <summary>
/// A fixed hash key for tests (ADR-025). The <c>Hash</c> policy is keyed and the kernel refuses to
/// start without a key; tests that use a model with <c>[TrackHash]</c> fields pass
/// <see cref="Provider"/> to their store, or register it as the <c>IHashKeyProvider</c> of a host.
/// The key is published, so the hashes it produces protect nothing: configured as
/// <c>HashMasterKey</c>, the kernel refuses to start (review P-16).
/// </summary>
public static class TestHashKeys
{
    private static readonly byte[] MasterKeyBytes = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    /// <summary>The key id of the test key.</summary>
    public const string KeyId = "test";

    /// <summary>Gets a key provider for the test key.</summary>
    public static IHashKeyProvider Provider { get; } = new MasterKeyHashKeyProvider(MasterKeyBytes, KeyId);
}
