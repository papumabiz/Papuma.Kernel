// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Serialization;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// A declared property whose JSON name contains a dot is one path segment: its path is escaped, so it
/// cannot be mistaken for a nested property, and policies and patches address it correctly.
/// </summary>
public sealed class DottedPropertyNameTests
{
    private sealed record Dotted(
        string Id,
        [property: JsonPropertyName("a.b"), SensitiveData] string Weird,
        string Plain);

    [Fact]
    public void Build_AcceptsAPropertyThatSerializesWithADot_AndEscapesItsPolicyPath()
    {
        var metadata = new KernelModelBuilder().Document<Dotted>().Build().GetRequired<Dotted>();

        Assert.Equal(FieldPolicy.Redact, metadata.Policies["a\\.b"]);
        Assert.False(metadata.Policies.ContainsKey("a.b"));
        Assert.Equal(FieldPolicy.Track, metadata.ResolvePolicy("plain"));
    }

    [Fact]
    public void PatchBuilder_AddressesTheDottedPropertyAsOneSegment()
    {
        var builder = new PatchBuilder<Dotted>().Set(x => x.Weird, "x");

        var operation = Assert.Single(builder.Operations);
        Assert.Equal(["a.b"], operation.PathSegments);
    }
}
