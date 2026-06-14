// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Model;

/// <summary>
/// Controls how a field appears in change feed diffs and event payloads (ADR-007).
/// </summary>
public enum FieldPolicy
{
    /// <summary>Old and new value are recorded verbatim (default).</summary>
    Track = 0,

    /// <summary>Only the fact that the field changed is recorded — no values.</summary>
    Redact = 1,

    /// <summary>A reference to the value's location is recorded; the value stays in the document.</summary>
    Reference = 2,

    /// <summary>A change marker plus a SHA-256 hash of the new value is recorded.</summary>
    Hash = 3,

    /// <summary>The field never appears in diffs at all.</summary>
    DoNotTrack = 4,
}
