// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Model;

/// <summary>
/// A declared key on a document type, materialized as a partial expression index (ADR-006).
/// </summary>
/// <param name="Path">The dot-separated JSON path of the key field.</param>
/// <param name="Unique">Whether the key is unique per scope.</param>
/// <param name="IndexName">The PostgreSQL index name (used to map 23505 violations back to the key).</param>
public sealed record KeyMetadata(string Path, bool Unique, string IndexName)
{
    /// <summary>Gets the path split into segments for <c>#&gt;&gt;</c> expressions.</summary>
    public string[] PathSegments { get; } = Path.Split('.');
}
