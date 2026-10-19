// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Local.Tests.Infrastructure;

/// <summary>
/// The fixed hash key of this suite (ADR-025) — the same published test key as
/// <c>Papuma.Kernel.Testing.TestHashKeys</c>, which the SQLite suite does not reference because that
/// package is built around a PostgreSQL container. Never a real key.
/// </summary>
internal static class TestHashKeys
{
    public const string KeyId = "test";

    public static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    public static IHashKeyProvider Provider { get; } = new MasterKeyHashKeyProvider(MasterKey, KeyId);
}
