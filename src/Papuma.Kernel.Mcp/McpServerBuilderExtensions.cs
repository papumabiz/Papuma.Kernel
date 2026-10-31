// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

namespace Papuma.Kernel.Mcp;

/// <summary>
/// Registration sugar for the Papuma MCP tools (phase 13).
/// </summary>
/// <example>
/// Inside the application that hosts the kernel (the model lives there):
/// <code>
/// builder.Services
///     .AddMcpServer()
///     .WithHttpTransport()                 // or WithStdioServerTransport()
///     .WithPapumaKernel(o => o with
///     {
///         // The document tools read in the caller's scope; without a resolver they refuse.
///         // The scope the application's middleware resolved (needs AddHttpContextAccessor):
///         ScopeResolver = r => r.Services.GetRequiredService&lt;IHttpContextAccessor&gt;().HttpContext is { } http
///             &amp;&amp; http.TryGetScopeContext(out var scope) ? scope : null,
///     });
///
/// // Opt-in to mutating tools (retry, checkpoint reset):
/// .WithPapumaKernel(o => o with { ScopeResolver = …, AllowMutations = true });
/// </code>
/// </example>
public static class McpServerBuilderExtensions
{
    /// <summary>
    /// Registers the Papuma kernel tools on an MCP server. Requires
    /// <c>AddPapumaKernel(...)</c> on the same service collection — the tools resolve
    /// the store and both feed processors from there.
    /// </summary>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="configure">Optional options transform (read-only defaults).</param>
    /// <exception cref="InvalidOperationException">The Papuma tools are already registered on this service collection.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> returned <c>null</c>.</exception>
    public static IMcpServerBuilder WithPapumaKernel(
        this IMcpServerBuilder builder,
        Func<PapumaMcpOptions, PapumaMcpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(d => d.ServiceType == typeof(PapumaMcpOptions)))
        {
            throw new InvalidOperationException(
                "WithPapumaKernel() was already called on this service collection — register the Papuma tools once.");
        }

        var options = new PapumaMcpOptions();
        if (configure is not null)
        {
            options = configure(options)
                ?? throw new ArgumentNullException(nameof(configure), "The options transform returned null.");
        }

        builder.Services.AddSingleton(options);
        return builder.WithTools<PapumaKernelTools>();
    }
}
