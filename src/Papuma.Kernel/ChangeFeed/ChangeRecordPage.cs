// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.ChangeFeed;

/// <summary>
/// Represents a page of <see cref="ChangeRecord"/> results from a keyset-paginated query.
/// </summary>
/// <param name="Records">The records on this page, ordered by sequence id ascending.</param>
/// <param name="HasMore">
/// <see langword="true"/> if there are more records beyond this page.
/// Use <see cref="NextCursorSequenceId"/> as the <c>afterSequenceId</c> argument
/// of the next call to retrieve the following page.
/// </param>
/// <param name="NextCursorSequenceId">
/// The sequence id to pass as <c>afterSequenceId</c> on the next call.
/// <see langword="null"/> when <see cref="HasMore"/> is <see langword="false"/>.
/// </param>
public sealed record ChangeRecordPage(
    IReadOnlyList<ChangeRecord> Records,
    bool HasMore,
    long? NextCursorSequenceId);
