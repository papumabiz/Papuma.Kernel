// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Testing;

/// <summary>
/// Thrown by <c>DrainAsync</c> when a handler failed while draining, or the feed did not
/// settle within the cycle limit.
/// </summary>
public sealed class FeedDrainException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FeedDrainException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="failures">The recorded handler failures; empty when the cycle limit was hit.</param>
    public FeedDrainException(string message, IReadOnlyList<FeedFailure> failures)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(failures);
        Failures = failures;
    }

    /// <summary>Gets the recorded handler failures; empty when the cycle limit was hit.</summary>
    public IReadOnlyList<FeedFailure> Failures { get; }
}
