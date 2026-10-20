// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;

namespace Papuma.Kernel.Store;

/// <summary>
/// Reads the timestamps the SQLite kernel stores as round-trip (<c>"O"</c>) text.
/// </summary>
internal static class SqliteTimestamp
{
    /// <summary>
    /// Parses a stored timestamp independent of the current culture. A plain
    /// <c>DateTimeOffset.Parse</c> reads the year of an ISO text in the current culture's default
    /// calendar (<c>th-TH</c>: Buddhist), which silently shifts it or makes the parse fail
    /// (review L-10).
    /// </summary>
    /// <param name="text">The stored text.</param>
    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
