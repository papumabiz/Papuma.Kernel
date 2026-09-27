// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Application schema — typically projection tables — applied at startup right after the
/// SQLite kernel schema and before the feed workers start, so handlers never run against
/// a table that does not exist yet. Counterpart of the PostgreSQL kernel's
/// <c>ISchemaContributor</c>; register with
/// <see cref="PapumaKernelLocalBuilder.AddSchemaContributor{TContributor}"/>.
/// </summary>
/// <remarks>
/// Runs on every startup, only when <see cref="PapumaKernelLocalOptions.EnsureSchema"/> is
/// enabled, in registration order, on the connection the kernel used for its own schema
/// (WAL mode and busy timeout already applied). Keep it idempotent
/// (<c>CREATE TABLE IF NOT EXISTS</c>, <c>CREATE INDEX IF NOT EXISTS</c>). SQLite has no
/// <c>ADD COLUMN IF NOT EXISTS</c> — check <c>pragma_table_info</c> first. No lock is
/// needed: one process owns the database file.
/// </remarks>
public interface ISqliteSchemaContributor
{
    /// <summary>Creates or updates the application's schema objects.</summary>
    /// <param name="connection">An open connection to the kernel's database file.</param>
    /// <param name="ct">A cancellation token.</param>
    Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct);
}
