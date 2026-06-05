// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Events;

namespace Papuma.Kernel.Tests.Events;

public class OutboxExtensionsTests
{
    [Fact]
    public void AddOutboxWorker_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => OutboxExtensions.AddOutboxWorker<StubOutboxPublisher>(
            services: null!));
    }

    [Fact]
    public void AddOutboxWorker_RegistersPublisherAndHostedWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddOutboxWorker<StubOutboxPublisher>(options => options.BatchSize = 5);

        using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<StubOutboxPublisher>();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.NotNull(publisher);
        Assert.Single(hostedServices);
        Assert.IsType<OutboxWorker>(hostedServices[0]);
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");

    private sealed class StubOutboxPublisher : IOutboxPublisher
    {
        public Task<bool> PublishAsync(
            Papuma.Kernel.Tenancy.ScopeContext scope,
            Guid eventId,
            string eventType,
            string payloadJson,
            CancellationToken ct = default) => Task.FromResult(true);
    }
}