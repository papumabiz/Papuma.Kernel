// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.ChangeFeed;

/// <summary>
/// Configuration for <see cref="ChangeWriter"/>.
/// </summary>
public sealed class ChangeWriterOptions
{
    /// <summary>
    /// Gets or sets the maximum allowed payload size in bytes.
    /// </summary>
    public int MaxPayloadSizeBytes { get; set; } = 256 * 1024;
}