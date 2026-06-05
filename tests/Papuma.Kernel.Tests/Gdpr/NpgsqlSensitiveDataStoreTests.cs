// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Gdpr;

public class NpgsqlSensitiveDataStoreTests
{
    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new NpgsqlSensitiveDataStore(dataSource: null!));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForEmptySensitiveRef()
    {
        using var dataSource = CreateDataSource();
        var sut = new NpgsqlSensitiveDataStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.AppendAsync(
            scope: ScopeContext.Tenant("acme"),
            sensitiveRef: default,
            schemaVersion: 1,
            payloadJson: "{}",
            actorId: "user:1"));

        Assert.Equal("sensitiveRef", exception.ParamName);
    }

    [Fact]
    public async Task AppendAsync_ThrowsForInvalidSchemaVersion()
    {
        using var dataSource = CreateDataSource();
        var sut = new NpgsqlSensitiveDataStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.AppendAsync(
            scope: ScopeContext.Tenant("acme"),
            sensitiveRef: SensitiveRef.New(),
            schemaVersion: 0,
            payloadJson: "{}",
            actorId: "user:1"));

        Assert.Equal("version", exception.ParamName);
    }

    [Fact]
    public async Task MarkRedactedAsync_ThrowsForMissingReason()
    {
        using var dataSource = CreateDataSource();
        var sut = new NpgsqlSensitiveDataStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.MarkRedactedAsync(
            scope: ScopeContext.Tenant("acme"),
            sensitiveRef: SensitiveRef.New(),
            actorId: "user:1",
            reason: " "));

        Assert.Equal("reason", exception.ParamName);
    }

    [Fact]
    public async Task SetLegalHoldAsync_ThrowsForMissingReason()
    {
        using var dataSource = CreateDataSource();
        var sut = new NpgsqlSensitiveDataStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.SetLegalHoldAsync(
            scope: ScopeContext.Tenant("acme"),
            sensitiveRef: SensitiveRef.New(),
            enabled: true,
            actorId: "user:1",
            reason: ""));

        Assert.Equal("reason", exception.ParamName);
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}