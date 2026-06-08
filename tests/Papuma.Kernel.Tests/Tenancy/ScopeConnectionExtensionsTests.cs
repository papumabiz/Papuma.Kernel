// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public class ScopeConnectionExtensionsTests
{
    [Fact]
    public async Task SetScopeAsync_ThrowsForNullConnection()
    {
        var scope = ScopeContext.Platform();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ScopeConnectionExtensions.SetScopeAsync(null!, scope));
    }

    [Fact]
    public async Task SetScopeAsync_ThrowsForNullScope()
    {
        // We cannot create a real NpgsqlConnection without a database,
        // but the null-check for scope happens before any I/O.
        // Use reflection or just verify the guard fires with a stub.
        // Since NpgsqlConnection is sealed and cannot be mocked easily,
        // we verify the ArgumentNullException for the scope parameter
        // by passing a real (but unconnected) connection.
        using var dataSource = Npgsql.NpgsqlDataSource.Create(
            "Host=localhost;Port=1;Database=test;Username=test;Password=test");
        await using var conn = dataSource.CreateConnection();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => conn.SetScopeAsync(null!));
    }
    [Fact]
    public async Task SetAllScopesAsync_ThrowsForNullConnection()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ScopeConnectionExtensions.SetAllScopesAsync(null!));
    }
}
