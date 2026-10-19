// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Serialization;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// <c>[JsonIgnore(Condition = WhenWritingNull)]</c> only omits a null: the property is in the document whenever
/// it has a value, so its privacy policy must apply. Only a property that is never serialized has no path.
/// </summary>
public sealed class ConditionalIgnoreTests
{
    private sealed record Doc(
        string Id,
        [property: SensitiveData, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret,
        [property: TrackHash, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? Pin,
        [property: DoNotTrack, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scratch,
        [property: SensitiveData, JsonIgnore] string? NeverSerialized,
        [property: SensitiveData, JsonIgnore(Condition = JsonIgnoreCondition.Always)] string? AlsoNever);

    [Fact]
    public void AConditionallyIgnoredProperty_KeepsItsPolicy()
    {
        var policies = new KernelModelBuilder().Document<Doc>().Build().GetRequired<Doc>().Policies;

        Assert.Equal(FieldPolicy.Redact, policies["secret"]);
        Assert.Equal(FieldPolicy.Hash, policies["pin"]);
        Assert.Equal(FieldPolicy.DoNotTrack, policies["scratch"]);
    }

    [Fact]
    public void ANeverSerializedProperty_HasNoPath_AndNoPolicy()
    {
        var policies = new KernelModelBuilder().Document<Doc>().Build().GetRequired<Doc>().Policies;

        Assert.False(policies.ContainsKey("neverSerialized"));
        Assert.False(policies.ContainsKey("alsoNever"));
    }
}
