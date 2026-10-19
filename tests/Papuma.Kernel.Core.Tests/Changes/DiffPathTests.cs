// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>The escaped path form of a diff, a policy, a key and a GDPR selector.</summary>
public sealed class DiffPathTests
{
    [Theory]
    [InlineData("name", "name")]
    [InlineData("feature.enabled", "feature\\.enabled")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a.\\", "a\\.\\\\")]
    public void EscapeSegment_EscapesTheSeparatorAndItself(string segment, string escaped)
    {
        Assert.Equal(escaped, DiffPath.EscapeSegment(segment));
    }

    [Fact]
    public void APathWithoutADotOrBackslash_IsUnchanged_SoExistingDataIsUntouched()
    {
        Assert.Equal("address.city", DiffPath.Join(["address", "city"]));
        Assert.Equal("address.city", DiffPath.Append("address", "city"));
        Assert.Equal(["address", "city"], DiffPath.Split("address.city"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a", "b", "c")]
    [InlineData("a.b", "c")]
    [InlineData("a", "b.c")]
    [InlineData("a\\", "b")]
    [InlineData("\\", ".")]
    [InlineData("..", "\\\\")]
    public void JoinThenSplit_RoundTrips(params string[] segments)
    {
        Assert.Equal(segments, DiffPath.Split(DiffPath.Join(segments)));
    }

    [Theory]
    [InlineData("a.b.c", true, "a.b")]
    [InlineData("a.b", true, "a")]
    [InlineData("a", false, "")]
    [InlineData("a\\.b", false, "")]           // one segment "a.b"
    [InlineData("x.a\\.b", true, "x")]
    [InlineData("a\\\\.b", true, "a\\\\")]   // segment "a\", then "b"
    public void TryGetParent_FindsTheLastUnescapedSeparator(string path, bool hasParent, string parent)
    {
        Assert.Equal(hasParent, DiffPath.TryGetParent(path, out var actual));
        Assert.Equal(parent, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a\\")]       // dangling escape
    [InlineData("a\\x")]      // unknown escape
    public void Split_RejectsAMalformedPath(string path)
    {
        Assert.Throws<ArgumentException>(() => DiffPath.Split(path));
    }
}
