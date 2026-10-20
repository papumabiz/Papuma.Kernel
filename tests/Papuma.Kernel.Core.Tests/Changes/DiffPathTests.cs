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
    [InlineData("")]               // the empty key at the top: path ""
    [InlineData("a", "")]          // "a."
    [InlineData("", "a")]          // ".a"
    [InlineData("", "")]           // "."
    [InlineData("a", "", "b")]     // "a..b"
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
    [InlineData("a.", true, "a")]              // the empty key below a
    [InlineData(".", true, "")]                // the empty key below the empty top-level key
    [InlineData("", false, "")]                // the empty top-level key
    public void TryGetParent_FindsTheLastUnescapedSeparator(string path, bool hasParent, string parent)
    {
        Assert.Equal(hasParent, DiffPath.TryGetParent(path, out var actual));
        Assert.Equal(parent, actual);
    }

    [Theory]
    [InlineData("", new[] { "" })]
    [InlineData("a.", new[] { "a", "" })]
    [InlineData(".a", new[] { "", "a" })]
    [InlineData("a..b", new[] { "a", "", "b" })]
    public void Split_NamesEmptyKeys_AsEmptySegments(string path, string[] segments)
    {
        Assert.Equal(segments, DiffPath.Split(path));
    }

    [Fact]
    public void Append_TreatsNullAsTheRoot_AndTheEmptyStringAsTheEmptyKey()
    {
        Assert.Equal("a", DiffPath.Append(null, "a"));
        Assert.Equal(".a", DiffPath.Append("", "a"));
        Assert.Equal("", DiffPath.Append(null, ""));
        Assert.Equal("a.", DiffPath.Append("a", ""));
    }

    [Theory]
    [InlineData("a\\")]       // dangling escape
    [InlineData("a\\x")]      // unknown escape
    public void Split_RejectsAMalformedPath(string path)
    {
        Assert.Throws<ArgumentException>(() => DiffPath.Split(path));
    }
}
