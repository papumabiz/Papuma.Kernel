// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Gdpr;

public class GdprProcessorTests
{
    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new GdprProcessor(
            dataSource: null!,
            new ChangeWriter(),
            NullLogger<GdprProcessor>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullChangeWriter()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new GdprProcessor(
            dataSource,
            changeWriter: null!,
            NullLogger<GdprProcessor>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullLogger()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new GdprProcessor(
            dataSource,
            new ChangeWriter(),
            logger: null!));
    }

    [Fact]
    public async Task RedactEntityAsync_ThrowsForMissingActorId()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.RedactEntityAsync(
            scope: ScopeContext.Tenant("acme"),
            entity: "User",
            entityId: "user-1",
            actorId: " ",
            reason: "GDPR request"));

        Assert.Equal("actorId", exception.ParamName);
    }

    [Fact]
    public async Task RedactEntityAsync_ThrowsForMissingReason()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.RedactEntityAsync(
            scope: ScopeContext.Tenant("acme"),
            entity: "User",
            entityId: "user-1",
            actorId: "admin:1",
            reason: ""));

        Assert.Equal("reason", exception.ParamName);
    }

    [Fact]
    public async Task GetEntityHistoryAsync_ThrowsForMissingEntityId()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.GetEntityHistoryAsync(
            scope: ScopeContext.Tenant("acme"),
            entity: "User",
            entityId: " "));

        Assert.Equal("entityId", exception.ParamName);
    }

    [Fact]
    public async Task RedactEntityAsync_ThrowsForNullScope()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RedactEntityAsync(
            scope: null!,
            entity: "User",
            entityId: "user-1",
            actorId: "admin:1",
            reason: "GDPR request"));
    }

    [Fact]
    public async Task GetEntityHistoryAsync_ThrowsForNullScope()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.GetEntityHistoryAsync(
            scope: null!,
            entity: "User",
            entityId: "user-1"));
    }

    [Theory]
    [InlineData("1User")]
    [InlineData("U")]
    [InlineData("User-Profile")]
    public async Task RedactEntityAsync_ThrowsForInvalidEntity(string entity)
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.RedactEntityAsync(
            scope: ScopeContext.Tenant("acme"),
            entity: entity,
            entityId: "user-1",
            actorId: "admin:1",
            reason: "GDPR request"));
    }

    [Theory]
    [InlineData("1User")]
    [InlineData("U")]
    [InlineData("User-Profile")]
    public async Task GetEntityHistoryAsync_ThrowsForInvalidEntity(string entity)
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.GetEntityHistoryAsync(
            scope: ScopeContext.Tenant("acme"),
            entity: entity,
            entityId: "user-1"));
    }

    private static GdprProcessor CreateSut(NpgsqlDataSource dataSource) =>
        new(dataSource, new ChangeWriter(), NullLogger<GdprProcessor>.Instance);

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}
