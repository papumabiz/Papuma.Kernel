// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Mcp;

/// <summary>
/// Options for the Papuma MCP tools. Read-only is the default posture (phase 13):
/// an AI agent may inspect, but mutating operations require explicit opt-in.
/// </summary>
public sealed record PapumaMcpOptions
{
    /// <summary>The default for <see cref="MaxRecordsPerCall"/>.</summary>
    public const int DefaultMaxRecordsPerCall = 500;

    private readonly int _maxRecordsPerCall = DefaultMaxRecordsPerCall;

    /// <summary>
    /// Gets a value indicating whether mutating tools (<c>retry_feed_failure</c>,
    /// <c>reset_feed_checkpoint</c>) are enabled. Default <c>false</c>.
    /// </summary>
    public bool AllowMutations { get; init; }

    /// <summary>
    /// Gets the most records one tool call returns (document history, changes by
    /// correlation, feed failures — per feed). More records are cut off and the result
    /// says <c>"truncated": true</c>, so a single call cannot flood the agent's context or
    /// the server's memory. Default <see cref="DefaultMaxRecordsPerCall"/>; must be
    /// greater than zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not greater than zero.</exception>
    public int MaxRecordsPerCall
    {
        get => _maxRecordsPerCall;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxRecordsPerCall = value;
        }
    }
}
