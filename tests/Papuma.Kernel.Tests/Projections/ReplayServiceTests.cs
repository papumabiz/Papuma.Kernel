// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.Tests.Projections;

public class ReplayServiceTests
{
    [Fact]
    public void Constructor_ThrowsForNullWorkers()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayService(workers: null!));
    }

    [Fact]
    public async Task RequestReplayAsync_ThrowsForUnknownProjection()
    {
        var sut = new ReplayService(new Dictionary<string, ProjectionWorker>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RequestReplayAsync("missing"));
    }

    [Fact]
    public async Task RequestReplayAsync_DelegatesToKnownWorker()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
        var worker = new ProjectionWorker(
            new StubProjectionHandler(),
            dataSource,
            NullLogger<ProjectionWorker>.Instance);

        var sut = new ReplayService(new Dictionary<string, ProjectionWorker>
        {
            ["stub_projection"] = worker,
        });

        await sut.RequestReplayAsync("stub_projection");
    }

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