// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Testcontainers.PostgreSql;

namespace Papuma.Kernel.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL 18 container for all integration tests in the collection.
/// Exposes a superuser data source plus a non-superuser application role so that
/// RLS behavior (which superusers bypass) can be tested realistically.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Login role subject to RLS (no superuser, not table owner).</summary>
    public const string AppRoleName = "papuma_app";
    private const string AppRolePassword = "papuma_app_pw";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    private NpgsqlDataSource? _dataSource;
    private NpgsqlDataSource? _appRoleDataSource;

    /// <summary>Gets the superuser data source (RLS does not apply).</summary>
    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>Gets a data source connecting as the non-superuser application role (RLS applies).</summary>
    public NpgsqlDataSource AppRoleDataSource =>
        _appRoleDataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE ROLE {AppRoleName} LOGIN PASSWORD '{AppRolePassword}';
            """;
        await cmd.ExecuteNonQueryAsync();

        var appRoleBuilder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = AppRoleName,
            Password = AppRolePassword,
        };
        _appRoleDataSource = NpgsqlDataSource.Create(appRoleBuilder.ConnectionString);
    }

    /// <summary>
    /// Grants the application role access to all current tables in the papuma schema.
    /// Call after <c>EnsureSchemaAsync</c> so the grants cover the created tables.
    /// </summary>
    public async Task GrantAppRoleAccessAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            GRANT USAGE ON SCHEMA papuma TO {AppRoleName};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA papuma TO {AppRoleName};
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_appRoleDataSource is not null)
        {
            await _appRoleDataSource.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
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
