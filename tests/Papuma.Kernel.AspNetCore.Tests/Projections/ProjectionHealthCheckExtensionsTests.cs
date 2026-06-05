// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Papuma.Kernel.AspNetCore.Projections;

namespace Papuma.Kernel.AspNetCore.Tests.Projections;

public class ProjectionHealthCheckExtensionsTests
{
    [Fact]
    public void AddPapumaProjectionHealthChecks_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectionHealthCheckExtensions.AddPapumaProjectionHealthChecks(
            services: null!));
    }

    [Fact]
    public void AddPapumaProjectionHealthChecks_RegistersHealthCheckService()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPapumaProjectionHealthChecks();

        using var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetService<HealthCheckService>();

        Assert.NotNull(healthCheckService);
    }
}