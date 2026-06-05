// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.Tests.Projections;

public class ExternalProjectionExtensionsTests
{
    [Fact]
    public void AddExternalProjection_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectionExtensions.AddExternalProjection<StubExternalProjectionHandler>(
            services: null!));
    }

    [Fact]
    public void AddExternalProjection_RegistersHandlerAndHostedWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddExternalProjection<StubExternalProjectionHandler>(options => options.BatchSize = 5);

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<StubExternalProjectionHandler>();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.NotNull(handler);
        Assert.Single(hostedServices);
        Assert.IsType<ExternalProjectionWorker>(hostedServices[0]);
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