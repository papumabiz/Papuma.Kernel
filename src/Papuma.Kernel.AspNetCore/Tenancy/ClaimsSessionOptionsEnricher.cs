// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Claims;

using Microsoft.AspNetCore.Http;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Default <see cref="ISessionOptionsEnricher"/> that extracts:
/// <list type="bullet">
///   <item><see cref="SessionOptions.ActorId"/> from the <c>sub</c> claim (or <c>NameIdentifier</c>)</item>
///   <item><see cref="SessionOptions.CausationType"/> from the endpoint display name (Minimal API) or action name (MVC)</item>
///   <item><see cref="SessionOptions.CausationId"/> from the <see cref="HttpContext.TraceIdentifier"/></item>
/// </list>
/// <para>
/// Register with <c>services.AddPapumaSessionOptions&lt;ClaimsSessionOptionsEnricher&gt;()</c>.
/// For custom claim types or naming conventions, implement <see cref="ISessionOptionsEnricher"/> directly.
/// </para>
/// </summary>
public sealed class ClaimsSessionOptionsEnricher : ISessionOptionsEnricher
{
    /// <inheritdoc />
    public SessionOptions Enrich(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? context.User.FindFirstValue("sub");

        var endpoint = context.GetEndpoint();
        var causationType = endpoint?.DisplayName;

        return new SessionOptions
        {
            ActorId = actorId,
            CausationType = causationType,
            CausationId = context.TraceIdentifier,
        };
    }
}
