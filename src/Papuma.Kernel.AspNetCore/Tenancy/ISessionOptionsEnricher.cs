// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Http;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.AspNetCore.Tenancy;

/// <summary>
/// Enriches <see cref="SessionOptions"/> from the current HTTP request — typically
/// by extracting <see cref="SessionOptions.ActorId"/> from claims and
/// <see cref="SessionOptions.CausationType"/> from the endpoint metadata.
/// <para>
/// Register an implementation via
/// <see cref="SessionOptionsExtensions.AddPapumaSessionOptions{TEnricher}"/> and
/// retrieve the enriched options with <see cref="SessionOptionsExtensions.GetSessionOptions"/>.
/// </para>
/// </summary>
public interface ISessionOptionsEnricher
{
    /// <summary>
    /// Builds session options for the current request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The enriched session options.</returns>
    SessionOptions Enrich(HttpContext context);
}
