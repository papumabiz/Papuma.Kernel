// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.AspNetCore.Http;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Resolves the scope context from an ASP.NET Core request.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the tenant trust boundary.</b> Whatever the resolver returns decides which
/// tenant's data the request can read and write; the kernel trusts it completely. Derive the
/// tenant only from <em>authenticated</em> data — a claim of <c>HttpContext.User</c> issued
/// by your identity provider — never from something the caller can set freely, such as an
/// <c>X-Tenant-Id</c> header, a query string or a route value (that is tenant spoofing).
/// If a header or route value selects the tenant, check it against the authenticated user's
/// permitted tenants before returning it.
/// </para>
/// <para>
/// Pipeline order: <c>UseScopeResolution()</c> must run <em>after</em>
/// <c>UseAuthentication()</c> (and normally <c>UseAuthorization()</c>); before that
/// <c>HttpContext.User</c> is anonymous and a claim-based resolver sees no tenant.
/// </para>
/// <para>
/// Fail closed: for a request without a usable tenant, throw (an
/// <see cref="ArgumentException"/>, which <see cref="ScopeMiddleware"/> answers with
/// <c>400</c>, or any other exception for a server error) instead of falling back to
/// <see cref="ScopeContext.Platform"/>, which sees every tenant. Return <c>null</c> never —
/// the middleware rejects it with an <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public interface IScopeResolver
{
    /// <summary>
    /// Resolves the scope context for the specified request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The scope for this request; never <see langword="null"/>.</returns>
    ScopeContext Resolve(HttpContext context);
}
