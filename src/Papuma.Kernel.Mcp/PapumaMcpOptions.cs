// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Mcp;

/// <summary>
/// Options for the Papuma MCP tools. Read-only is the default posture (phase 13):
/// an AI agent may inspect, but mutating operations require explicit opt-in.
/// </summary>
public sealed record PapumaMcpOptions
{
    /// <summary>
    /// Gets a value indicating whether mutating tools (<c>retry_feed_failure</c>,
    /// <c>reset_feed_checkpoint</c>) are enabled. Default <c>false</c>.
    /// </summary>
    public bool AllowMutations { get; init; }
}
