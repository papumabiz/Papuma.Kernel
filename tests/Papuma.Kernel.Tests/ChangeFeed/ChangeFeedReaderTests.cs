// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.ChangeFeed;

public class ChangeFeedReaderTests
{
    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new ChangeFeedReader(dataSource: null!));
    }

    [Fact]
    public async Task GetByEntityAsync_ThrowsForNullScope()
    {
        using var dataSource = CreateDataSource();
        var sut = new ChangeFeedReader(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.GetByEntityAsync(
            scope: null!,
            entity: "User",
            entityId: "123"));
    }

    [Fact]
    public async Task GetByEntityAsync_ThrowsForInvalidEntity()
    {
        using var dataSource = CreateDataSource();
        var sut = new ChangeFeedReader(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.GetByEntityAsync(
            ScopeContext.Platform(),
            entity: "1",
            entityId: "123"));
    }

    [Fact]
    public async Task GetByEntityAsync_ThrowsForLimitBelowOne()
    {
        using var dataSource = CreateDataSource();
        var sut = new ChangeFeedReader(dataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.GetByEntityAsync(
            ScopeContext.Platform(),
            entity: "User",
            entityId: "123",
            limit: 0));
    }

    [Fact]
    public async Task GetBySequenceRangeAsync_ThrowsForFromSequenceIdBelowOne()
    {
        using var dataSource = CreateDataSource();
        var sut = new ChangeFeedReader(dataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.GetBySequenceRangeAsync(
            ScopeContext.Platform(),
            fromSequenceId: 0,
            toSequenceId: 5));
    }

    [Fact]
    public async Task GetBySequenceRangeAsync_ThrowsForToSequenceIdBelowFrom()
    {
        using var dataSource = CreateDataSource();
        var sut = new ChangeFeedReader(dataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.GetBySequenceRangeAsync(
            ScopeContext.Platform(),
            fromSequenceId: 5,
            toSequenceId: 4));
    }

    [Fact]
    public async Task GetLatestSequenceIdAsync_ThrowsForNullScope()
    {
        using var dataSource = CreateDataSource();
        var sut = new ChangeFeedReader(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.GetLatestSequenceIdAsync(scope: null!));
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}