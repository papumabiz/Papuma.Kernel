// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Events;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Events;

public class OutboxWorkerTests
{
    [Fact]
    public void Constructor_ThrowsForNullPublisher()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new OutboxWorker(
            publisher: null!,
            dataSource,
            NullLogger<OutboxWorker>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new OutboxWorker(
            new StubOutboxPublisher(),
            dataSource: null!,
            NullLogger<OutboxWorker>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullLogger()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new OutboxWorker(
            new StubOutboxPublisher(),
            dataSource,
            logger: null!));
    }

    [Fact]
    public void Constructor_ThrowsForInvalidBatchSize()
    {
        using var dataSource = CreateDataSource();

        var options = new OutboxWorkerOptions { BatchSize = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxWorker(
            new StubOutboxPublisher(),
            dataSource,
            NullLogger<OutboxWorker>.Instance,
            options));
    }

    [Fact]
    public void Constructor_ThrowsForMaxRetryDelayBelowBaseRetryDelay()
    {
        using var dataSource = CreateDataSource();

        var options = new OutboxWorkerOptions
        {
            BaseRetryDelay = TimeSpan.FromSeconds(2),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxWorker(
            new StubOutboxPublisher(),
            dataSource,
            NullLogger<OutboxWorker>.Instance,
            options));
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");

    private sealed class StubOutboxPublisher : IOutboxPublisher
    {
        public Task<bool> PublishAsync(
            ScopeContext scope,
            Guid eventId,
            string eventType,
            string payloadJson,
            CancellationToken ct = default) => Task.FromResult(true);
    }
}