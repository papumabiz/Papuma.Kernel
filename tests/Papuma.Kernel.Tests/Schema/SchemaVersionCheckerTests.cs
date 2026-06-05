// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Schema;

namespace Papuma.Kernel.Tests.Schema;

public class SchemaVersionCheckerTests
{
    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new SchemaVersionChecker(dataSource: null!));
    }

    [Fact]
    public async Task EnsureMinimumVersionAsync_ThrowsForVersionBelowOne()
    {
        using var dataSource = CreateDataSource();
        var sut = new SchemaVersionChecker(dataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.EnsureMinimumVersionAsync(0));
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}