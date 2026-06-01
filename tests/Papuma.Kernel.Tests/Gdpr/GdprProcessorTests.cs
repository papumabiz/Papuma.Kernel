// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Events;
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
            new BusinessEventWriter(),
            NullLogger<GdprProcessor>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullBusinessEventWriter()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new GdprProcessor(
            dataSource,
            businessEventWriter: null!,
            NullLogger<GdprProcessor>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullLogger()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new GdprProcessor(
            dataSource,
            new BusinessEventWriter(),
            logger: null!));
    }

    [Fact]
    public async Task RedactEntityAsync_ThrowsForMissingActorId()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => sut.RedactEntityAsync(
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
            entity: "User",
            entityId: " "));

        Assert.Equal("entityId", exception.ParamName);
    }

    [Fact]
    public async Task RedactEntityAsync_ThrowsForNullTenant()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RedactEntityAsync(
            tenant: null!,
            entity: "User",
            entityId: "user-1",
            actorId: "admin:1",
            reason: "GDPR request"));
    }

    [Fact]
    public async Task GetEntityHistoryAsync_ThrowsForNullTenant()
    {
        using var dataSource = CreateDataSource();
        var sut = CreateSut(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.GetEntityHistoryAsync(
            tenant: null!,
            entity: "User",
            entityId: "user-1"));
    }

    private static GdprProcessor CreateSut(NpgsqlDataSource dataSource) =>
        new(dataSource, new BusinessEventWriter(), NullLogger<GdprProcessor>.Instance);

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}