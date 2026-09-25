// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Testing;

namespace Papuma.Kernel.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL 18 container for all integration tests in the collection — built on
/// <see cref="PapumaTestDatabase"/>, so the kernel tests itself with the same setup its
/// consumers get. Exposes the superuser data source plus a non-superuser application role
/// so that RLS behavior (which superusers bypass) can be tested realistically.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Login role subject to RLS (no superuser, not table owner).</summary>
    public const string AppRoleName = PapumaTestDatabase.DefaultAppRoleName;

    private PapumaTestDatabase? _database;

    /// <summary>Gets the test database.</summary>
    public PapumaTestDatabase Database =>
        _database ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>Gets the superuser data source (RLS does not apply).</summary>
    public NpgsqlDataSource DataSource => Database.OwnerDataSource;

    /// <summary>Gets a data source connecting as the non-superuser application role (RLS applies).</summary>
    public NpgsqlDataSource AppRoleDataSource => Database.AppDataSource;

    /// <inheritdoc />
    public async Task InitializeAsync() => _database = await PapumaTestDatabase.StartAsync();

    /// <summary>
    /// Grants the application role access to all current tables in the papuma schema.
    /// Call after <c>EnsureSchemaAsync</c> so the grants cover the created tables.
    /// </summary>
    public Task GrantAppRoleAccessAsync() => Database.GrantAppRoleAsync("papuma");

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

/// <summary>
/// xUnit collection binding all integration tests to one shared container.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name used by integration test classes.</summary>
    public const string Name = "postgres";
}
