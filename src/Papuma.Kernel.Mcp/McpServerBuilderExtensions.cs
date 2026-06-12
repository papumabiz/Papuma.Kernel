// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

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
///     .WithPapumaKernel();                 // read-only default
///
/// // Opt-in to mutating tools (retry, checkpoint reset):
/// .WithPapumaKernel(o => o with { AllowMutations = true });
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
    public static IMcpServerBuilder WithPapumaKernel(
        this IMcpServerBuilder builder,
        Func<PapumaMcpOptions, PapumaMcpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new PapumaMcpOptions();
        if (configure is not null)
        {
            options = configure(options);
        }

        builder.Services.AddSingleton(options);
        return builder.WithTools<PapumaKernelTools>();
    }
}
