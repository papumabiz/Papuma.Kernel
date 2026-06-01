// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.Tests.Projections;

public class ProjectionExtensionsReplayTests
{
    [Fact]
    public void AddReplayService_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProjectionExtensions.AddReplayService(services: null!));
    }

    [Fact]
    public void AddReplayService_RegistersReplayServiceWithDiscoveredWorkers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProjectionWorker>>(
            NullLogger<ProjectionWorker>.Instance);

        services.AddProjection<StubProjectionHandler>();
        services.AddReplayService();

        using var provider = services.BuildServiceProvider();
        var replayService = provider.GetRequiredService<ReplayService>();

        Assert.NotNull(replayService);
    }

    [Fact]
    public void ProjectionWorker_ProjectionName_ReturnsHandlerName()
    {
        using var dataSource = CreateDataSource();
        var worker = new ProjectionWorker(
            new StubProjectionHandler(),
            dataSource,
            NullLogger<ProjectionWorker>.Instance);

        Assert.Equal("stub_replay_projection", worker.ProjectionName);
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");

    private sealed class StubProjectionHandler : IProjectionHandler
    {
        public string Name => "stub_replay_projection";

        public IReadOnlyCollection<string> EventTypes { get; } = ["TestEvent"];

        public Task HandleAsync(
            ChangeRecord record,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
