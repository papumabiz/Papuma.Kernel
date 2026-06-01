// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Transactions;

namespace Papuma.Kernel;

/// <summary>
/// Top-level configuration for all Papuma.Kernel services registered via
/// <see cref="ServiceCollectionExtensions.AddPapumaKernel"/>.
/// </summary>
public sealed class PapumaKernelOptions
{
    /// <summary>
    /// Maximum payload size in bytes shared by all writers. Default: 256 KB.
    /// </summary>
    public int MaxPayloadSizeBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Unit of work retry configuration. <c>null</c> uses the built-in defaults.
    /// </summary>
    public UnitOfWorkOptions? UnitOfWork { get; set; }
}
