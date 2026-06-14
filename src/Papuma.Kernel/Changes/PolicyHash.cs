// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The single hash function for the <see cref="Model.FieldPolicy.Hash"/> policy, shared
/// by the feed diff (<see cref="PolicyApplier"/>) and the read projection
/// (<see cref="PolicyProjector"/>). Sharing it guarantees the invariant of ADR-016:
/// a policy-projected read shows exactly the same hash the feed shows.
/// </summary>
internal static class PolicyHash
{
    /// <summary>Returns the lowercase SHA-256 hex of a value's JSON serialization.</summary>
    public static string Of(JsonNode? value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null")));
}
