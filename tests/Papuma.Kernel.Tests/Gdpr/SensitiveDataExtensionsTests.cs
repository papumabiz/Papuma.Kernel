// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Papuma.Kernel.Gdpr;

namespace Papuma.Kernel.Tests.Gdpr;

public class SensitiveDataExtensionsTests
{
    [Fact]
    public void AddSensitiveDataStore_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => SensitiveDataExtensions.AddSensitiveDataStore(services: null!));
    }

    [Fact]
    public void AddSensitiveDataStore_RegistersStoreAndResolver()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateDataSource());

        services.AddSensitiveDataStore();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<NpgsqlSensitiveDataStore>());
        Assert.NotNull(provider.GetRequiredService<ISensitiveDataStore>());
        Assert.NotNull(provider.GetRequiredService<ISensitiveDataResolver>());
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}