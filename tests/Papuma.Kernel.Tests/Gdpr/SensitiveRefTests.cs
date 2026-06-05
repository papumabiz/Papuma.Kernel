// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Gdpr;

namespace Papuma.Kernel.Tests.Gdpr;

public class SensitiveRefTests
{
    [Fact]
    public void New_ReturnsNonEmptyReference()
    {
        var reference = SensitiveRef.New();

        Assert.False(reference.IsEmpty);
        Assert.NotEqual(Guid.Empty, reference.Value);
    }

    [Fact]
    public void IsEmpty_ReturnsTrueForDefault()
    {
        var reference = default(SensitiveRef);

        Assert.True(reference.IsEmpty);
    }
}