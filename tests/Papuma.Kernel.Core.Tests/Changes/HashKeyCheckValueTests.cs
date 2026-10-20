// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>The key-check value of ADR-025 point 5 and its comparison.</summary>
public sealed class HashKeyCheckValueTests
{
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    // From docs/spec/vectors/hash.json (keyCheck), produced by the Python reference.
    private const string VectorCheckValue = "660ce59ef9e2b24acfb8dbd3641aebc94f04082d0ad96516b55def6b6efb3203";

    [Fact]
    public void Compute_MatchesTheSpecVector_ForTheDefaultProvider()
    {
        var provider = new MasterKeyHashKeyProvider(MasterKey, "test");

        Assert.Equal(VectorCheckValue, HashKeyCheckValue.Compute(provider));
        Assert.Equal(VectorCheckValue, HashV2.KeyCheck(MasterKey));
    }

    [Fact]
    public void Compute_DependsOnTheKey_NotOnTheKeyId()
    {
        var other = (byte[])MasterKey.Clone();
        other[0] ^= 0xFF;

        Assert.NotEqual(
            HashKeyCheckValue.Compute(new MasterKeyHashKeyProvider(MasterKey, "k1")),
            HashKeyCheckValue.Compute(new MasterKeyHashKeyProvider(other, "k1")));
        Assert.Equal(
            HashKeyCheckValue.Compute(new MasterKeyHashKeyProvider(MasterKey, "k1")),
            HashKeyCheckValue.Compute(new MasterKeyHashKeyProvider(MasterKey, "k2")));
    }

    [Fact]
    public void Verify_AcceptsEqualValues()
    {
        HashKeyCheckValue.Verify("k1", VectorCheckValue, VectorCheckValue);
    }

    [Fact]
    public void Verify_ThrowsTyped_AndNeverPrintsAValue()
    {
        var other = VectorCheckValue[..^1] + (VectorCheckValue[^1] == '0' ? "1" : "0");

        var ex = Assert.Throws<HashKeyMismatchException>(() => HashKeyCheckValue.Verify("k1", VectorCheckValue, other));

        Assert.Equal("k1", ex.KeyId);
        Assert.Contains("'k1'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(VectorCheckValue, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(other, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsAValueOfADifferentLength()
    {
        Assert.Throws<HashKeyMismatchException>(() => HashKeyCheckValue.Verify("k1", VectorCheckValue, "abc"));
    }

    [Fact]
    public void Verify_GuardsItsArguments()
    {
        Assert.Equal("keyId", Assert.Throws<ArgumentNullException>(() => HashKeyCheckValue.Verify(null!, VectorCheckValue, VectorCheckValue)).ParamName);
        Assert.Equal("computed", Assert.Throws<ArgumentNullException>(() => HashKeyCheckValue.Verify("k1", null!, VectorCheckValue)).ParamName);
        Assert.Equal("stored", Assert.Throws<ArgumentNullException>(() => HashKeyCheckValue.Verify("k1", VectorCheckValue, null!)).ParamName);
    }
}
