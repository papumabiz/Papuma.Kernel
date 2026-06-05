// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Schema;

namespace Papuma.Kernel.Tests.Schema;

public class SchemaExtensionsTests
{
    [Fact]
    public void AddSchemaVersionChecker_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaExtensions.AddSchemaVersionChecker(services: null!));
    }

    [Fact]
    public void AddSchemaVersionChecker_ReturnsSameCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddSchemaVersionChecker();

        Assert.Same(services, result);
    }
}