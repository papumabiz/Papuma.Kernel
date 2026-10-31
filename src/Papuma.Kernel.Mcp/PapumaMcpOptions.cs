// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Tenancy;

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
    /// Gets the binding of the scope-bound tools (<c>get_document</c>, <c>get_document_by_key</c>,
    /// <c>get_document_history</c>, <c>get_changes_by_correlation</c>) to the caller (ADR-016). Called once per tool
    /// call; the scope it returns is the one the tool reads in. A <c>tenantId</c> argument that names another scope is
    /// refused, and an omitted one means the returned scope. <c>null</c> as the result refuses the call. Without a
    /// resolver those tools refuse every call — the agent does not choose the tenant (review M-1).
    /// </summary>
    /// <example>
    /// <code>
    /// // From the scope the application's middleware resolved for the HTTP request (needs AddHttpContextAccessor):
    /// o => o with
    /// {
    ///     ScopeResolver = r => r.Services.GetRequiredService&lt;IHttpContextAccessor&gt;().HttpContext is { } http
    ///         &amp;&amp; http.TryGetScopeContext(out var scope) ? scope : null,
    /// }
    ///
    /// // An operator tool on a management port, where every caller may read every tenant:
    /// o => o with { ScopeResolver = McpScopeResolvers.TrustToolArgument }
    /// </code>
    /// </example>
    public Func<McpScopeRequest, ScopeContext?>? ScopeResolver { get; init; }

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
