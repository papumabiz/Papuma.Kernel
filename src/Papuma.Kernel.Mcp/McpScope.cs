// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Mcp;

/// <summary>
/// What a <see cref="PapumaMcpOptions.ScopeResolver"/> gets for one call of a scope-bound tool.
/// </summary>
/// <param name="Services">
/// The services of the call — under the HTTP transport the request's services, so
/// <c>IHttpContextAccessor</c> reaches the authenticated principal.
/// </param>
/// <param name="Requested">
/// The scope the agent asked for with the tool's <c>tenantId</c> argument, or <c>null</c> when it omitted it.
/// Untrusted: it is what the agent — or a prompt injected into it — chose.
/// </param>
public sealed record McpScopeRequest(IServiceProvider Services, ScopeContext? Requested);

/// <summary>
/// Ready-made values for <see cref="PapumaMcpOptions.ScopeResolver"/>.
/// </summary>
public static class McpScopeResolvers
{
    /// <summary>
    /// Lets the agent choose: the <c>tenantId</c> argument's tenant, or the platform scope when it is omitted — the
    /// behaviour before 3.0. Only for an operator tool whose every caller may read every tenant, behind a management
    /// port or network rule; a prompt injection can then read any tenant whose ids it learns.
    /// </summary>
    /// <param name="request">The call's scope request.</param>
    /// <returns>The requested scope, or the platform scope.</returns>
    public static ScopeContext? TrustToolArgument(McpScopeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Requested ?? ScopeContext.Platform();
    }
}
