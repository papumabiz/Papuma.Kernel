// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Handles a specific version of a versioned event payload.
/// </summary>
/// <typeparam name="TPayload">The strongly typed payload handled by this implementation.</typeparam>
public interface IVersionedHandler<TPayload>
{
    /// <summary>
    /// Gets the version handled by this implementation.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Handles the supplied payload and raw change record.
    /// </summary>
    /// <param name="data">The deserialized payload.</param>
    /// <param name="record">The raw change record.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleAsync(TPayload data, ChangeRecord record, CancellationToken ct = default);
}