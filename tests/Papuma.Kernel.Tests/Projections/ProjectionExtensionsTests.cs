// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.Tests.Projections;

public class ProjectionExtensionsTests
{
    [Fact]
    public void AddProjection_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectionExtensions.AddProjection<StubProjectionHandler>(
            services: null!));
    }

    [Fact]
    public void AddProjection_RegistersHandlerAndHostedWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProjectionWorker>>(NullLogger<ProjectionWorker>.Instance);

        services.AddProjection<StubProjectionHandler>(options => options.BatchSize = 5);

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<StubProjectionHandler>();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.NotNull(handler);
        Assert.Single(hostedServices);
        Assert.IsType<ProjectionWorker>(hostedServices[0]);
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