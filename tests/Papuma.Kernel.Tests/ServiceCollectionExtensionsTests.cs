// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel;
using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Events;
using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Transactions;

namespace Papuma.Kernel.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPapumaKernel_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ServiceCollectionExtensions.AddPapumaKernel(services: null!));
    }

    [Fact]
    public void AddPapumaKernel_RegistersAllCoreServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddPapumaKernel();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ChangeWriter>());
        Assert.NotNull(provider.GetRequiredService<BusinessEventWriter>());
        Assert.NotNull(provider.GetRequiredService<OutboxWriter>());
        Assert.NotNull(provider.GetRequiredService<GdprProcessor>());
        Assert.NotNull(provider.GetRequiredService<IUnitOfWork>());
    }

    [Fact]
    public void AddPapumaKernel_AppliesCustomOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddPapumaKernel(options =>
        {
            options.MaxPayloadSizeBytes = 512 * 1024;
        });

        using var provider = services.BuildServiceProvider();

        var changeWriterOptions = provider.GetRequiredService<ChangeWriterOptions>();
        Assert.Equal(512 * 1024, changeWriterOptions.MaxPayloadSizeBytes);

        var businessEventWriterOptions = provider.GetRequiredService<BusinessEventWriterOptions>();
        Assert.Equal(512 * 1024, businessEventWriterOptions.MaxPayloadSizeBytes);

        var outboxWriterOptions = provider.GetRequiredService<OutboxWriterOptions>();
        Assert.Equal(512 * 1024, outboxWriterOptions.MaxPayloadSizeBytes);
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}
