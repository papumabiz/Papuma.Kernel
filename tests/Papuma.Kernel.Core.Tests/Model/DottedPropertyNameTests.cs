// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Serialization;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// Paths are dot-separated, so a JSON property name containing '.' is ambiguous. The diff
/// engine rejects such keys on the first save; the model refuses them up front.
/// </summary>
public sealed class DottedPropertyNameTests
{
    private sealed record Dotted(string Id, [property: JsonPropertyName("a.b")] string Weird);

    private sealed record Plain(string Id, string Name);

    [Fact]
    public void Build_RejectsAPropertyThatSerializesWithADot()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => new KernelModelBuilder().Document<Dotted>().Build());

        Assert.Contains("a.b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchBuilder_RejectsAPropertyThatSerializesWithADot()
    {
        Assert.Throws<NotSupportedException>(() => new PatchBuilder<Dotted>().Set(x => x.Weird, "x"));
    }

    [Fact]
    public void Build_AcceptsOrdinaryProperties()
    {
        var model = new KernelModelBuilder().Document<Plain>().Build();

        Assert.NotNull(model.GetRequired<Plain>());
    }
}
