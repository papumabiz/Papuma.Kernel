// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Testing;

/// <summary>
/// A fixed hash key for tests (ADR-025). The <c>Hash</c> policy is keyed and the kernel refuses to
/// start without a key; tests that use a model with <c>[TrackHash]</c> fields pass
/// <see cref="Provider"/> to their store, or set <see cref="MasterKeyBase64"/> and
/// <see cref="KeyId"/> in the hosting options. <b>Never use this key outside tests</b> — it is
/// published, so the hashes it produces protect nothing.
/// </summary>
public static class TestHashKeys
{
    private static readonly byte[] MasterKeyBytes = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    /// <summary>The key id of the test key.</summary>
    public const string KeyId = "test";

    /// <summary>Gets the test master key, base64 — the form <c>PapumaKernelOptions.HashMasterKey</c> takes.</summary>
    public static string MasterKeyBase64 { get; } = Convert.ToBase64String(MasterKeyBytes);

    /// <summary>Gets a key provider for the test key.</summary>
    public static IHashKeyProvider Provider { get; } = new MasterKeyHashKeyProvider(MasterKeyBytes, KeyId);
}
