// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Testing;

/// <summary>
/// What <see cref="PapumaTestDatabase.ConnectAsync"/> may do to a role on a server that is not
/// its own (review P-17), and when the role it created goes away (review P-20).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PapumaTestDatabaseRoleTests
{
    private readonly PostgresFixture _fixture;

    public PapumaTestDatabaseRoleTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static string NewRoleName() => $"papuma_test_{Guid.NewGuid():N}";

    private Task<PapumaTestDatabase> ConnectAsync(string role) =>
        PapumaTestDatabase.ConnectAsync(_fixture.Database.OwnerConnectionString, role);

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> RoleExistsAsync(string role)
    {
        await using var cmd = _fixture.DataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @name)");
        cmd.Parameters.AddWithValue("name", role);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(bool Super, bool CreateRole)> RoleFlagsAsync(string role)
    {
        await using var cmd = _fixture.DataSource.CreateCommand("SELECT rolsuper, rolcreaterole FROM pg_roles WHERE rolname = @name");
        cmd.Parameters.AddWithValue("name", role);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private async Task<string> CurrentUserAsync()
    {
        await using var cmd = _fixture.DataSource.CreateCommand("SELECT current_user");
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    // ── P-17: roles that must not be touched ───────────────────────────────────

    [Fact]
    public async Task ConnectAsync_RejectsTheConnectingAccount_AndLeavesItAlone()
    {
        var owner = await CurrentUserAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ConnectAsync(owner));

        Assert.Equal("appRoleName", ex.ParamName);
        Assert.Contains(owner, ex.Message);
        Assert.True((await RoleFlagsAsync(owner)).Super); // not demoted ...
        await using var stillWorks = new NpgsqlConnection(_fixture.Database.OwnerConnectionString + ";Pooling=false");
        await stillWorks.OpenAsync(); // ... and the password was not rotated
    }

    [Theory]
    [InlineData("SUPERUSER")]
    [InlineData("CREATEROLE")]
    public async Task ConnectAsync_RejectsAnExistingPrivilegedRole_AndLeavesItAlone(string privilege)
    {
        var role = NewRoleName();
        await ExecuteAsync($"CREATE ROLE {role} NOLOGIN {privilege}");
        try
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => ConnectAsync(role));

            Assert.Equal("appRoleName", ex.ParamName);
            var (superuser, createRole) = await RoleFlagsAsync(role);
            Assert.Equal(privilege == "SUPERUSER", superuser);
            Assert.Equal(privilege == "CREATEROLE", createRole);
        }
        finally
        {
            await ExecuteAsync($"DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task ConnectAsync_StillResetsAnOrdinaryExistingRole()
    {
        var role = NewRoleName();
        await ExecuteAsync($"CREATE ROLE {role} NOLOGIN CREATEDB");
        try
        {
            await using var db = await ConnectAsync(role);
            await using var conn = await db.AppDataSource.OpenConnectionAsync(); // now LOGIN with a fresh password

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_user";
            Assert.Equal(role, (string?)await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await ExecuteAsync($"DROP ROLE {role}");
        }
    }

    // ── P-20: DropRoleOnDispose ────────────────────────────────────────────────

    [Fact]
    public async Task DropRoleOnDispose_IsOffByDefault_TheRoleSurvives()
    {
        var role = NewRoleName();
        await using (var db = await ConnectAsync(role))
        {
            Assert.False(db.DropRoleOnDispose);
        }

        try
        {
            Assert.True(await RoleExistsAsync(role));
        }
        finally
        {
            await ExecuteAsync($"DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task DropRoleOnDispose_RemovesTheRoleItCreated_EvenWithGrantsAndOpenConnections()
    {
        var role = NewRoleName();
        var db = await ConnectAsync(role);
        db.DropRoleOnDispose = true;
        await db.GrantAppRoleAsync("public"); // a privilege the drop has to clear first
        await using (var conn = await db.AppDataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_user";
            Assert.Equal(role, (string?)await cmd.ExecuteScalarAsync());
        }

        Assert.True(await RoleExistsAsync(role));
        await db.DisposeAsync();

        Assert.False(await RoleExistsAsync(role));
    }

    [Fact]
    public async Task DropRoleOnDispose_LeavesAPreexistingRoleAlone()
    {
        var role = NewRoleName();
        await ExecuteAsync($"CREATE ROLE {role} NOLOGIN");
        try
        {
            var db = await ConnectAsync(role);
            db.DropRoleOnDispose = true; // asked for, but the role was not created by this instance
            await db.DisposeAsync();

            Assert.True(await RoleExistsAsync(role));
        }
        finally
        {
            await ExecuteAsync($"DROP ROLE {role}");
        }
    }
}
