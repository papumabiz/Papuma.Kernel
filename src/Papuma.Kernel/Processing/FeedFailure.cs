// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Processing;

/// <summary>
/// A persisted handler failure (retrying or poison) — the failure table as an API
/// (phase 11 diagnostics).
/// </summary>
/// <param name="HandlerName">The handler name (without internal feed prefixes).</param>
/// <param name="Seq">The feed sequence number of the failing record.</param>
/// <param name="Attempts">Attempts so far; at the engine's MaxAttempts the record is skipped as poison.</param>
/// <param name="LastError">Type and message of the last exception.</param>
/// <param name="NextRetryAt">When the next retry is due (meaningless once poisoned).</param>
/// <param name="UpdatedAt">When the failure entry was last touched.</param>
public sealed record FeedFailure(
    string HandlerName,
    long Seq,
    int Attempts,
    string LastError,
    DateTimeOffset NextRetryAt,
    DateTimeOffset UpdatedAt);
