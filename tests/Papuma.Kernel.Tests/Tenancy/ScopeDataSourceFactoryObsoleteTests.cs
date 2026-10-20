// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Reflection;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Tenancy;

public sealed class ScopeDataSourceFactoryObsoleteTests
{
    [Fact]
    public void ScopeDataSourceFactory_IsObsolete_AndSaysWhy()
    {
        // By name: referencing the type would itself raise the obsolete warning this test pins.
        var type = typeof(IScopeDataSourceFactory).Assembly.GetType("Papuma.Kernel.Tenancy.ScopeDataSourceFactory");

        var obsolete = Assert.IsType<ObsoleteAttribute>(
            Assert.Single(type!.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)));
        Assert.False(obsolete.IsError); // a warning, not a break: removal waits for the next major version
        Assert.Contains("evict", obsolete.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NpgsqlDataSource", obsolete.Message, StringComparison.Ordinal);
    }
}
