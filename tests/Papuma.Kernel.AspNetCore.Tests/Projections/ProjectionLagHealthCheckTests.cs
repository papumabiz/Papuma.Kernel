// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Papuma.Kernel.AspNetCore.Projections;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.AspNetCore.Tests.Projections;

public class ProjectionLagHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy_WhenNoProjectionWorkersAreRegistered()
    {
        var sut = new ProjectionLagHealthCheck(
            hostedServices: [],
            options: Options.Create(new ProjectionHealthCheckOptions { MaxAllowedLag = 5 }));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy_WhenAllLagsAreWithinThreshold()
    {
        var sut = new ProjectionLagHealthCheck(
            hostedServices: [new StubProjectionWorker("p1", 3)],
            options: Options.Create(new ProjectionHealthCheckOptions { MaxAllowedLag = 5 }));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsUnhealthy_WhenLagExceedsThreshold()
    {
        var sut = new ProjectionLagHealthCheck(
            hostedServices: [new StubProjectionWorker("p1", 9)],
            options: Options.Create(new ProjectionHealthCheckOptions { MaxAllowedLag = 5 }));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class StubProjectionWorker : BackgroundService, IProjectionLagProvider
    {
        private readonly long _lag;

        public StubProjectionWorker(string projectionName, long lag)
        {
            ProjectionName = projectionName;
            _lag = lag;
        }

        public string ProjectionName { get; }

        public Task<ProjectionLagSnapshot> GetLagSnapshotAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new ProjectionLagSnapshot(ProjectionName, 10, 10 + _lag, _lag));
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}