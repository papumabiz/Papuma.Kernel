// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Default <see cref="ISessionOptionsEnricher"/> that extracts:
/// <list type="bullet">
///   <item><see cref="SessionOptions.ActorId"/> from the <c>sub</c> claim (or <c>NameIdentifier</c>)</item>
///   <item>
///     <see cref="SessionOptions.CausationType"/> from the explicit endpoint name (Minimal API
///     <c>.WithName("PlaceOrder")</c>, or <c>[EndpointName]</c>) or, for MVC, <c>Controller.Action</c>;
///     <see langword="null"/> for an endpoint without such a name. The endpoint <em>display name</em> is not
///     used: it carries route templates, handler and assembly names that would end up in stored data.
///   </item>
///   <item><see cref="SessionOptions.CausationId"/> from the <see cref="HttpContext.TraceIdentifier"/></item>
/// </list>
/// <para>
/// The actor id is limited to <see cref="MaxActorIdLength"/> characters; a longer claim is a
/// misconfigured identity provider (or a forged token the application should not have accepted)
/// and makes <see cref="Enrich"/> throw rather than store a truncated, ambiguous identity.
/// </para>
/// <para>
/// Register with <c>services.AddPapumaSessionOptions&lt;ClaimsSessionOptionsEnricher&gt;()</c>.
/// For custom claim types or naming conventions, implement <see cref="ISessionOptionsEnricher"/> directly.
/// </para>
/// </summary>
public sealed class ClaimsSessionOptionsEnricher : ISessionOptionsEnricher
{
    /// <summary>
    /// The longest actor id the enricher accepts (OpenID Connect limits <c>sub</c> to 255 characters).
    /// </summary>
    public const int MaxActorIdLength = 256;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The actor id claim is longer than <see cref="MaxActorIdLength"/>.</exception>
    public SessionOptions Enrich(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? context.User.FindFirstValue("sub");

        if (actorId is { Length: > MaxActorIdLength })
        {
            throw new InvalidOperationException(
                $"The actor id claim has {actorId.Length} characters; at most {MaxActorIdLength} are accepted.");
        }

        var causationType = CausationTypeOf(context.GetEndpoint());

        return new SessionOptions
        {
            ActorId = actorId,
            CausationType = causationType,
            CausationId = context.TraceIdentifier,
        };
    }

    private static string? CausationTypeOf(Endpoint? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        // Read from the endpoint's static metadata, not from request route values: a Minimal API
        // route with an {action} parameter would put client input there.
        return endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
            ? $"{action.ControllerName}.{action.ActionName}"
            : null;
    }
}
