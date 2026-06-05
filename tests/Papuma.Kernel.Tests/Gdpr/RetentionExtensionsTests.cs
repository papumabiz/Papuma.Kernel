// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Gdpr;

namespace Papuma.Kernel.Tests.Gdpr;

public class RetentionExtensionsTests
{
    [Fact]
    public void AddRetentionWorker_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => RetentionExtensions.AddRetentionWorker(services: null!));
    }

    [Fact]
    public void AddRetentionWorker_RegistersHostedWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddRetentionWorker(options => options.BatchSize = 10);

        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Single(hostedServices);
        Assert.IsType<RetentionWorker>(hostedServices[0]);
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}