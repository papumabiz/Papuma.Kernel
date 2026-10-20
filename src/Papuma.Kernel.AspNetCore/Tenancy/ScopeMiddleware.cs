// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Resolves and stores the scope context for the current HTTP request.
/// </summary>
public sealed class ScopeMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopeMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public ScopeMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Resolves scope context and stores it in <see cref="HttpContext.Items"/>.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="resolver">The scope resolver implementation.</param>
    /// <remarks>
    /// An <see cref="ArgumentException"/> thrown by the resolver (for example from
    /// <see cref="ScopeContext.Tenant(string)"/> for a malformed tenant id the client sent) is
    /// answered with <c>400 Bad Request</c> and the rest of the pipeline does not run. An
    /// <see cref="ArgumentNullException"/> is treated as a bug in the resolver and propagates,
    /// like every other exception, as does an exception from the rest of the pipeline.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The resolver returned <see langword="null"/>.</exception>
    public async Task InvokeAsync(HttpContext context, IScopeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);

        ScopeContext scope;
        try
        {
            scope = resolver.Resolve(context);
        }
        catch (ArgumentException ex) when (ex is not ArgumentNullException)
        {
            // The exception message is deliberately not logged or returned: for a custom
            // resolver it may echo client input.
            context.RequestServices?.GetService<ILogger<ScopeMiddleware>>()?.LogInformation(
                "Request rejected with 400: {Resolver} reported an invalid scope ({ExceptionType}).",
                resolver.GetType().Name,
                ex.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (scope is null)
        {
            throw new InvalidOperationException(
                $"{resolver.GetType().FullName}.Resolve returned null. A scope resolver must return a " +
                "ScopeContext (for example ScopeContext.Tenant(...) or ScopeContext.Platform()) or throw.");
        }

        context.Items[ScopeMiddlewareExtensions.ScopeContextItemKey] = scope;
        await _next(context);
    }
}
