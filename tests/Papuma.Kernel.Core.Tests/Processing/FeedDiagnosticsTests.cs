// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;

using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Core.Tests.Processing;

/// <summary>The error text stored in the failure table (review C-12).</summary>
public sealed class FeedDiagnosticsTests
{
    private const int Limit = 200;

    private static string Message(Exception ex) => FeedDiagnostics.SanitizeError(ex);

    [Theory]
    [InlineData(Limit - 1)] // the pair would start on the last kept unit
    [InlineData(Limit)]
    [InlineData(Limit + 1)]
    public void ASurrogatePairAtTheLimit_IsNeverCutInHalf(int pairStart)
    {
        // "😀" is two UTF-16 units; place it so the 200-unit cut falls inside, at or after it.
        var text = new string('a', pairStart) + "😀" + "tail";

        var stored = Message(new InvalidOperationException(text));

        AssertWellFormed(stored);
        Assert.EndsWith("…", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutInsideAPair_DropsTheWholePair()
    {
        var text = new string('a', Limit - 1) + "😀" + "tail";

        var stored = Message(new InvalidOperationException(text));

        Assert.Equal("InvalidOperationException: " + new string('a', Limit - 1) + "…", stored);
    }

    [Fact]
    public void AMessageOfExactlyTheLimit_IsKeptWhole()
    {
        var text = new string('a', Limit - 2) + "😀";

        var stored = Message(new InvalidOperationException(text));

        Assert.Equal("InvalidOperationException: " + text, stored);
    }

    [Fact]
    public void ALongPlainMessage_IsStillCutAtTheLimit()
    {
        var stored = Message(new InvalidOperationException(new string('b', 500)));

        Assert.Equal("InvalidOperationException: " + new string('b', Limit) + "…", stored);
    }

    [Fact]
    public void ALongMessageOfSurrogatePairs_StaysWellFormed()
    {
        var stored = Message(new InvalidOperationException(string.Concat(Enumerable.Repeat("😀", 300))));

        AssertWellFormed(stored);
    }

    private static void AssertWellFormed(string text)
    {
        // Throws on a lone surrogate.
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(text);
    }
}
