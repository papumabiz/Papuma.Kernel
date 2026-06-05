// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.Tests.Projections;

public class ExternalProjectionWorkerTests
{
    [Fact]
    public void Constructor_ThrowsForNullHandler()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new ExternalProjectionWorker(
            handler: null!,
            dataSource,
            NullLogger<ExternalProjectionWorker>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new ExternalProjectionWorker(
            new StubExternalProjectionHandler(),
            dataSource: null!,
            NullLogger<ExternalProjectionWorker>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullLogger()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new ExternalProjectionWorker(
            new StubExternalProjectionHandler(),
            dataSource,
            logger: null!));
    }

    [Fact]
    public void Constructor_ThrowsForInvalidBatchSize()
    {
        using var dataSource = CreateDataSource();

        var options = new ProjectionWorkerOptions { BatchSize = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalProjectionWorker(
            new StubExternalProjectionHandler(),
            dataSource,
            NullLogger<ExternalProjectionWorker>.Instance,
            options));
    }

    [Fact]
    public void Constructor_ThrowsForMaxRetryDelayBelowBaseRetryDelay()
    {
        using var dataSource = CreateDataSource();

        var options = new ProjectionWorkerOptions
        {
            BaseRetryDelay = TimeSpan.FromSeconds(2),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalProjectionWorker(
            new StubExternalProjectionHandler(),
            dataSource,
            NullLogger<ExternalProjectionWorker>.Instance,
            options));
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");

    private sealed class StubExternalProjectionHandler : IExternalProjectionHandler
    {
        public string Name => "stub_external_projection";

        public IReadOnlyCollection<string> EventTypes { get; } = ["UserEmailUpdated"];

        public Task HandleAsync(ChangeRecord record, CancellationToken ct = default) => Task.CompletedTask;
    }
}