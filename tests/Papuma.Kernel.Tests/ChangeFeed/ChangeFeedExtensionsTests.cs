// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Tests.ChangeFeed;

public class ChangeFeedExtensionsTests
{
    [Fact]
    public void AddChangeFeedReader_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => ChangeFeedExtensions.AddChangeFeedReader(services: null!));
    }

    [Fact]
    public void AddChangeFeedReader_ReturnsSameCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddChangeFeedReader();

        Assert.Same(services, result);
    }
}