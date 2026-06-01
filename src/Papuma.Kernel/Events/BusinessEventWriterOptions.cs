// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Events;

/// <summary>
/// Configuration for <see cref="BusinessEventWriter"/>.
/// </summary>
public sealed class BusinessEventWriterOptions
{
    /// <summary>
    /// Gets or sets the maximum allowed payload size in bytes.
    /// </summary>
    public int MaxPayloadSizeBytes { get; set; } = 256 * 1024;
}
