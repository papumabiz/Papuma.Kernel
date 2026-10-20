// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Timestamps are stored as round-trip ("O") text and must read back the same under any
/// culture (review L-10). The review feared that <c>DateTimeOffset.Parse</c> without a culture
/// reads the year of an ISO text in a non-Gregorian default calendar (<c>th-TH</c>). Checked:
/// it does not for text in the "O" format (see the last test) — so <see cref="SqliteTimestamp"/> is
/// hardening (no dependence on the ambient culture for text from other writers), not a fix.
/// </summary>
public sealed class SqliteTimestampTests
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 20, 8, 30, 15, 123, TimeSpan.Zero);

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("")]
    public void Parse_ReadsAStoredTimestamp_ThePerCultureWayItWasWritten(string cultureName)
    {
        var text = Moment.ToString("O"); // exactly what the kernel writes
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            Assert.Equal(Moment, SqliteTimestamp.Parse(text));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Parse_KeepsTheOffsetOfTheStoredText()
    {
        var local = new DateTimeOffset(2026, 10, 20, 10, 30, 0, TimeSpan.FromHours(2));

        var parsed = SqliteTimestamp.Parse(local.ToString("O"));

        Assert.Equal(local, parsed);
        Assert.Equal(TimeSpan.FromHours(2), parsed.Offset);
    }

    [Fact]
    public void PlainParse_OfARoundtripText_IsAlreadyCultureIndependent_SoTheHelperIsHardeningNotAFix()
    {
        // Pins the finding that L-10 does not reproduce for the format the kernel writes: if a
        // future runtime changes this, the helper becomes a fix and this test says so.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            Assert.Equal(Moment, DateTimeOffset.Parse(Moment.ToString("O")));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
