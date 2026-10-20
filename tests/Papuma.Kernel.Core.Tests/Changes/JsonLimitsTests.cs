// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;
using System.Text.Json;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// How "too deep" is told apart from every other JSON failure — only that one becomes a
/// <see cref="DocumentTooDeepException"/>; malformed input and other serializer errors pass through unchanged.
/// </summary>
public sealed class JsonLimitsTests
{
    public sealed record Holder(object? Value);

    private static string Nested(int levels) =>
        new StringBuilder().Insert(0, "{\"n\":", levels - 1).Append("{}").Append('}', levels - 1).ToString();

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, true)]
    [InlineData(10_000, true)]
    public void TextExceedsMaxDepth_CountsTheRootAsTheFirstLevel(int levels, bool tooDeep)
    {
        Assert.Equal(tooDeep, JsonLimits.TextExceedsMaxDepth(Nested(levels)));
    }

    [Theory]
    [InlineData("""{"a":}""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("")]
    public void MalformedShallowText_IsNotTooDeep(string text)
    {
        Assert.False(JsonLimits.TextExceedsMaxDepth(text));
        Assert.ThrowsAny<JsonException>(() => DocumentJson.ParseStored(text, "Doc", "1")); // passes through
    }

    [Fact]
    public void ParseStored_ReportsDepth_AsDocumentTooDeep()
    {
        var ex = Assert.Throws<DocumentTooDeepException>(() => DocumentJson.ParseStored(Nested(65), "Doc", "1"));

        Assert.Equal("Doc", ex.DocumentType);
        Assert.Equal("1", ex.DocumentId);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Fact]
    public void AnOtherSerializerError_IsNotReportedAsTooDeep()
    {
        // A type the serializer cannot handle fails at a shallow path.
        var ex = Assert.ThrowsAny<Exception>(() => DocumentJson.Serialize(new Holder(new IntPtr(1)), "Doc", "1"));

        Assert.IsNotType<DocumentTooDeepException>(ex);
    }

    [Fact]
    public void TheSerializerErrorAtTheLimit_IsRecognizedByItsPath()
    {
        var shallow = new JsonException("x", "$.a.b", null, null);
        var deep = new JsonException("x", "$" + string.Concat(Enumerable.Repeat(".n", JsonLimits.MaxDepth)), null, null);
        var dottedNames = new JsonException("x", "$['a.b.c.d.e.f.g.h']", null, null);

        Assert.False(JsonLimits.IsSerializerDepthError(shallow));
        Assert.True(JsonLimits.IsSerializerDepthError(deep));
        Assert.False(JsonLimits.IsSerializerDepthError(dottedNames)); // dots inside a quoted name are no separators
        Assert.Equal(JsonLimits.MaxDepth + 1, KernelJson.Options.MaxDepth);
    }
}
