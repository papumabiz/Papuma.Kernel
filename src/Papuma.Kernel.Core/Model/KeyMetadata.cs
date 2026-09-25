// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Model;

/// <summary>
/// A declared key on a document type, materialized as a partial expression index (ADR-006).
/// A composite key spans several fields (ADR-020); its <see cref="Path"/> lists the
/// component paths comma-separated in declaration order, e.g. <c>projectId,number</c>.
/// </summary>
/// <param name="Path">The dot-separated JSON path of the key field; comma-separated component paths for a composite key.</param>
/// <param name="Unique">Whether the key is unique per scope.</param>
/// <param name="IndexName">The storage layer's unique index/constraint name backing this key (used to map constraint-violation errors back to the key).</param>
public sealed record KeyMetadata(string Path, bool Unique, string IndexName)
{
    /// <summary>The separator between component paths of a composite key in <see cref="Path"/>.</summary>
    public const char ComponentSeparator = ',';

    /// <summary>Gets the component paths in declaration order — one entry for a single-field key.</summary>
    public IReadOnlyList<string> Paths { get; } = Path.Split(ComponentSeparator);

    /// <summary>Gets whether the key spans more than one field (ADR-020).</summary>
    public bool IsComposite => Paths.Count > 1;

    /// <summary>Gets each component path split into segments for <c>#&gt;&gt;</c> expressions.</summary>
    public IReadOnlyList<string[]> ComponentSegments { get; } =
        [.. Path.Split(ComponentSeparator).Select(p => p.Split('.'))];

    /// <summary>
    /// Gets the path split into segments for <c>#&gt;&gt;</c> expressions. Single-field keys
    /// only; empty for a composite key — use <see cref="ComponentSegments"/> there.
    /// </summary>
    public string[] PathSegments { get; } = Path.Contains(ComponentSeparator) ? [] : Path.Split('.');
}
