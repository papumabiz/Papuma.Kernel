// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

using Testcontainers.PostgreSql;

namespace Papuma.Kernel.Testing;

/// <summary>
/// A PostgreSQL database for integration tests of a Papuma application, with two
/// connections: <see cref="OwnerDataSource"/> for setup and inspection, and
/// <see cref="AppDataSource"/> for the code under test — a login role that is neither
/// superuser nor table owner, so row-level security applies exactly as in production.
/// Tests that run as a superuser pass even when tenant isolation is broken.
/// </summary>
/// <remarks>
/// Test-framework agnostic: start one instance per test run (an xUnit collection
/// fixture, an NUnit <c>[OneTimeSetUp]</c>, …) and dispose it at the end. Tests share
/// the database, so isolate them by data, not by schema — a fresh
/// <c>ScopeContext.Tenant(Guid.NewGuid())</c> per test, a unique handler name per
/// feed processor, and document type names that are unique across test classes (key
/// indexes are per document type, database-wide).
/// </remarks>
public sealed partial class PapumaTestDatabase : IAsyncDisposable
{
    /// <summary>The container image used by <see cref="StartAsync"/> unless overridden.</summary>
    public const string DefaultImage = "postgres:18-alpine";

    /// <summary>The application role name used unless overridden.</summary>
    public const string DefaultAppRoleName = "papuma_test_app";

    private readonly PostgreSqlContainer? _container;
    private readonly bool _roleCreated;

    private PapumaTestDatabase(
        PostgreSqlContainer? container,
        string ownerConnectionString,
        NpgsqlDataSource ownerDataSource,
        NpgsqlDataSource appDataSource,
        string appRoleName,
        bool roleCreated)
    {
        _container = container;
        OwnerConnectionString = ownerConnectionString;
        OwnerDataSource = ownerDataSource;
        AppDataSource = appDataSource;
        AppRoleName = appRoleName;
        _roleCreated = roleCreated;
    }

    /// <summary>
    /// Gets the connection string of the owner or superuser account, password included —
    /// for tools that need their own connection (migrations, <c>psql</c>).
    /// </summary>
    public string OwnerConnectionString { get; }

    /// <summary>
    /// Gets the data source of the connecting (owner or superuser) account. Row-level
    /// security does not apply — use it for schema setup and for inspecting state,
    /// never for the code under test.
    /// </summary>
    public NpgsqlDataSource OwnerDataSource { get; }

    /// <summary>
    /// Gets the data source of the application role: no superuser, no table owner,
    /// so row-level security applies. Build stores and feed processors on this one.
    /// </summary>
    public NpgsqlDataSource AppDataSource { get; }

    /// <summary>Gets the name of the application role behind <see cref="AppDataSource"/>.</summary>
    public string AppRoleName { get; }

    /// <summary>
    /// Gets or sets whether <see cref="DisposeAsync"/> removes the application role again
    /// (default: <c>false</c>). Roles are server-wide and outlive the test run, which adds up
    /// on a long-lived server (<see cref="ConnectAsync"/>). Only a role this instance created is
    /// dropped — one that existed already may belong to something else and is left alone — and
    /// its privileges and the objects it owns in the connected database go with it
    /// (<c>DROP OWNED BY</c>). A container started by <see cref="StartAsync"/> is removed as a
    /// whole, so there the option has no effect.
    /// </summary>
    public bool DropRoleOnDispose { get; set; }

    /// <summary>
    /// Starts a PostgreSQL container (Testcontainers) and prepares the application role.
    /// Needs a container runtime (Docker, or Podman with a Docker-compatible socket).
    /// </summary>
    /// <param name="image">The container image; must be PostgreSQL 18 or later.</param>
    /// <param name="appRoleName">The application role to create.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<PapumaTestDatabase> StartAsync(
        string image = DefaultImage,
        string appRoleName = DefaultAppRoleName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ValidateRoleName(appRoleName);

        var container = new PostgreSqlBuilder(image).Build();
        try
        {
            await container.StartAsync(ct);
            return await CreateAsync(container, container.GetConnectionString(), appRoleName, ct);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Uses an existing PostgreSQL server (a CI service container, a local instance)
    /// and prepares the application role. The account in
    /// <paramref name="ownerConnectionString"/> needs the right to create roles; the
    /// role is created if missing and gets a fresh random password either way.
    /// </summary>
    /// <remarks>
    /// Roles are server-wide, not per database: test runs that share one server in
    /// parallel need distinct <paramref name="appRoleName"/>s, or each resets the other's
    /// password. An existing role is reset to <c>LOGIN NOSUPERUSER NOBYPASSRLS</c> with a new
    /// password, so only a dedicated test role qualifies: the connecting account itself and any
    /// role that is a superuser or may create roles are refused, untouched. The role outlives
    /// the run unless <see cref="DropRoleOnDispose"/> is set.
    /// </remarks>
    /// <param name="ownerConnectionString">Connection string of the owner or superuser account.</param>
    /// <param name="appRoleName">The application role to create or reuse.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="appRoleName"/> is not a valid role name, or names the connecting account or
    /// a superuser / <c>CREATEROLE</c> role.
    /// </exception>
    public static Task<PapumaTestDatabase> ConnectAsync(
        string ownerConnectionString,
        string appRoleName = DefaultAppRoleName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        ValidateRoleName(appRoleName);
        return CreateAsync(container: null, ownerConnectionString, appRoleName, ct);
    }

    /// <summary>
    /// Creates or updates the kernel schema for <paramref name="model"/> (idempotent, like
    /// at application startup) and grants the application role access to it. Call it
    /// once per model; several test classes with different models may share a database.
    /// </summary>
    /// <param name="model">The kernel model whose key indexes are materialized.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task EnsureSchemaAsync(KernelModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await SchemaManager.EnsureSchemaAsync(OwnerDataSource, model, ct);
        await GrantAppRoleAsync("papuma", ct);
    }

    /// <summary>
    /// Grants the application role usage of a schema and DML on all its current tables —
    /// for the application's own tables (projection targets, ADR-019), after creating them.
    /// Application schemas also get <c>TRUNCATE</c>, the reset of a projection under
    /// row-level security (ADR-024); <c>papuma</c> never does — <c>TRUNCATE</c> is not
    /// subject to RLS.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task GrantAppRoleAsync(string schema, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        await using var conn = await OwnerDataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var quotedSchema = QuoteIdentifier(schema);
        var truncate = schema == "papuma" ? string.Empty : ", TRUNCATE";
        cmd.CommandText = $"""
            GRANT USAGE ON SCHEMA {quotedSchema} TO {AppRoleName};
            GRANT SELECT, INSERT, UPDATE, DELETE{truncate} ON ALL TABLES IN SCHEMA {quotedSchema} TO {AppRoleName};
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Ensures the schema for <paramref name="model"/> and returns a store that runs as
    /// the application role — the way the application runs in production.
    /// </summary>
    /// <param name="model">The kernel model.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<DocumentStore> CreateStoreAsync(KernelModel model, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(model, ct);
        return new DocumentStore(AppDataSource, model, logger: null, TestHashKeys.Provider);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await AppDataSource.DisposeAsync();
        try
        {
            // The container goes away with its roles; on a shared server only the role created here is removed.
            if (DropRoleOnDispose && _roleCreated && _container is null)
            {
                await using var cmd = OwnerDataSource.CreateCommand(
                    $"DROP OWNED BY {AppRoleName}; DROP ROLE IF EXISTS {AppRoleName};");
                await cmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await OwnerDataSource.DisposeAsync();
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
    }

    private static async Task<PapumaTestDatabase> CreateAsync(
        PostgreSqlContainer? container,
        string ownerConnectionString,
        string appRoleName,
        CancellationToken ct)
    {
        var ownerDataSource = NpgsqlDataSource.Create(ownerConnectionString);
        try
        {
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            bool roleCreated;
            await using (var conn = await ownerDataSource.OpenConnectionAsync(ct))
            {
                roleCreated = !await RoleExistsAsync(conn, appRoleName, ct);
                await using var cmd = conn.CreateCommand();
                // Role name validated; password is hex. Neither can break out of the literal.
                cmd.CommandText = $"""
                    DO $$
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{appRoleName}') THEN
                            CREATE ROLE {appRoleName} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{password}';
                        ELSE
                            ALTER ROLE {appRoleName} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{password}';
                        END IF;
                    END
                    $$;
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var appConnectionString = new NpgsqlConnectionStringBuilder(ownerConnectionString)
            {
                Username = appRoleName,
                Password = password,
            }.ConnectionString;

            return new PapumaTestDatabase(
                container, ownerConnectionString, ownerDataSource, NpgsqlDataSource.Create(appConnectionString), appRoleName, roleCreated);
        }
        catch
        {
            await ownerDataSource.DisposeAsync();
            throw;
        }
    }

    // The role is reset below (new password, NOSUPERUSER, ...), so refuse before touching a role that
    // must not be: the account we are connected as, or one that is a superuser or may create roles
    // (review P-17). Returns whether the role exists.
    private static async Task<bool> RoleExistsAsync(NpgsqlConnection conn, string appRoleName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rolsuper, rolcreaterole, rolname = current_user FROM pg_roles WHERE rolname = @name";
        cmd.Parameters.AddWithValue("name", appRoleName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return false;
        }

        if (reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2))
        {
            throw new ArgumentException(
                $"Role '{appRoleName}' is the connecting account, a superuser or may create roles; " +
                "the test database resets its application role (password, NOSUPERUSER) and refuses to touch it. " +
                "Use a dedicated role name.",
                nameof(appRoleName));
        }

        return true;
    }

    private static void ValidateRoleName(string appRoleName)
    {
        if (appRoleName is null || !RoleNamePattern().IsMatch(appRoleName))
        {
            throw new ArgumentException(
                $"Invalid role name '{appRoleName}'. Must match [a-z_][a-z0-9_]{{0,62}}.", nameof(appRoleName));
        }
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex RoleNamePattern();
}
