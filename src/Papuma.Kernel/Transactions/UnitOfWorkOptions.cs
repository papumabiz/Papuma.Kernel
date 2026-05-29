// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Transactions;

/// <summary>
/// Configures retry behavior for <see cref="NpgsqlUnitOfWork"/>.
/// </summary>
public class UnitOfWorkOptions
{
    /// <summary>
    /// Maximum number of attempts for transient database failures.
    /// Must be greater than or equal to 1.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay between retry attempts. Jitter is added automatically.
    /// Must not be negative.
    /// </summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}