// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.Tests.Projections;

public class ProjectionWorkerTests
{
    [Fact]
    public void Constructor_ThrowsForNullHandler()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new ProjectionWorker(
            handler: null!,
            dataSource,
            NullLogger<ProjectionWorker>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectionWorker(
            new StubProjectionHandler(),
            dataSource: null!,
            NullLogger<ProjectionWorker>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullLogger()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new ProjectionWorker(
            new StubProjectionHandler(),
            dataSource,
            logger: null!));
    }

    [Fact]
    public void Constructor_ThrowsForInvalidBatchSize()
    {
        using var dataSource = CreateDataSource();

        var options = new ProjectionWorkerOptions { BatchSize = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectionWorker(
            new StubProjectionHandler(),
            dataSource,
            NullLogger<ProjectionWorker>.Instance,
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

        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectionWorker(
            new StubProjectionHandler(),
            dataSource,
            NullLogger<ProjectionWorker>.Instance,
            options));
    }

    [Fact]
    public void ProjectionName_MatchesLagProviderName()
    {
        using var dataSource = CreateDataSource();

        var worker = new ProjectionWorker(
            new StubProjectionHandler(),
            dataSource,
            NullLogger<ProjectionWorker>.Instance);

        var provider = Assert.IsAssignableFrom<IProjectionLagProvider>(worker);
        Assert.Equal(worker.ProjectionName, provider.ProjectionName);
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");

    private sealed class StubProjectionHandler : IProjectionHandler
    {
        public string Name => "stub_projection";

        public IReadOnlyCollection<string> EventTypes { get; } = ["UserEmailUpdated"];

        public Task HandleAsync(
            ChangeRecord record,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}