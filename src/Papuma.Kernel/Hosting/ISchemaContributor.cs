// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Application schema — typically projection tables and their row-level security
/// policies — applied at startup right after the kernel's own schema and before the feed
/// workers start, so handlers never run against a table that does not exist yet and
/// policies can use <c>papuma.scope_visible</c>/<c>scope_writable</c> (ADR-019).
/// Register with <see cref="PapumaKernelBuilder.AddSchemaContributor{TContributor}"/>.
/// </summary>
/// <remarks>
/// Runs on every startup, only when <see cref="PapumaKernelOptions.EnsureSchema"/> is
/// enabled, in registration order. Keep it idempotent (<c>IF NOT EXISTS</c>,
/// <c>DROP POLICY IF EXISTS</c> + <c>CREATE POLICY</c>), and serialize it when several
/// instances start at once (<c>pg_advisory_xact_lock</c>) — see the recipe
/// <c>docs/recipes/projection-schema.md</c>.
/// </remarks>
public interface ISchemaContributor
{
    /// <summary>Creates or updates the application's schema objects.</summary>
    /// <param name="dataSource">The kernel's data source.</param>
    /// <param name="ct">A cancellation token.</param>
    Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct);
}
