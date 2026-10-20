// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Reflection;

namespace Papuma.Kernel.Model;

/// <summary>
/// Reads the privacy policy attribute of a property (ADR-007): <see cref="DoNotTrackAttribute"/>,
/// <see cref="TrackHashAttribute"/>, <see cref="TrackReferenceAttribute"/>, <see cref="SensitiveDataAttribute"/>.
/// </summary>
internal static class PolicyAttributeReader
{
    /// <summary>
    /// Returns the policy a property declares and the name of its attribute, or <c>null</c> for none.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <exception cref="InvalidOperationException">
    /// The property carries more than one policy attribute. They used to be resolved silently by a precedence
    /// nobody had documented — and the winner was not the strongest protection (<c>[TrackHash]</c> beat
    /// <c>[SensitiveData]</c>, although a hash still allows equality correlation) (review C-16).
    /// </exception>
    public static (FieldPolicy Policy, string Attribute)? Of(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);

        (FieldPolicy Policy, string Attribute)? first = null;
        List<string>? all = null;

        void Note(FieldPolicy policy, string attribute)
        {
            first ??= (policy, attribute);
            (all ??= []).Add(attribute);
        }

        if (property.GetCustomAttribute<DoNotTrackAttribute>() is not null)
        {
            Note(FieldPolicy.DoNotTrack, "DoNotTrack");
        }

        if (property.GetCustomAttribute<TrackHashAttribute>() is not null)
        {
            Note(FieldPolicy.Hash, "TrackHash");
        }

        if (property.GetCustomAttribute<TrackReferenceAttribute>() is not null)
        {
            Note(FieldPolicy.Reference, "TrackReference");
        }

        if (property.GetCustomAttribute<SensitiveDataAttribute>() is not null)
        {
            Note(FieldPolicy.Redact, "SensitiveData");
        }

        if (all is { Count: > 1 })
        {
            throw new InvalidOperationException(
                $"Property {property.DeclaringType?.Name}.{property.Name} has more than one privacy policy attribute " +
                $"({string.Join(", ", all.Select(a => $"[{a}]"))}). Each property takes exactly one — decide which " +
                "protection applies: the kernel does not pick one for you. Remove all but one attribute, or remove " +
                "them all and configure the property fluently (ADR-007).");
        }

        return first;
    }
}
