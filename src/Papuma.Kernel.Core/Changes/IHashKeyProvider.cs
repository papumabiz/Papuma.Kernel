// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Supplies the keys of the keyed <see cref="Model.FieldPolicy.Hash"/> policy (ADR-025). The
/// default implementation, <see cref="MasterKeyHashKeyProvider"/>, derives a key per tenant and
/// type from one application master key; implement the interface to fetch real per-tenant keys
/// from a KMS.
/// </summary>
/// <remarks>
/// <see cref="GetKey"/> must be <b>pure and stable</b>: the same scope and type name return the
/// same bytes for as long as <see cref="ActiveKeyId"/> is unchanged — the kernel hashes on every
/// write and compares nothing else, so a key that changes underneath it silently breaks the
/// comparability the policy exists for. A new key means a new <see cref="ActiveKeyId"/>.
/// </remarks>
public interface IHashKeyProvider
{
    /// <summary>
    /// Gets the identifier of the key in use, written into every hash (<c>v2:&lt;kid&gt;:…</c>).
    /// 1 to 32 characters of <c>A-Z a-z 0-9 _ -</c>.
    /// </summary>
    string ActiveKeyId { get; }

    /// <summary>
    /// Returns the 32-byte key for one scope and type. <paramref name="typeName"/> is the document
    /// type, or the event type for event payloads.
    /// </summary>
    /// <param name="scope">The scope of the write or read (platform scope has an empty tenant id).</param>
    /// <param name="typeName">The logical document or event type name.</param>
    ReadOnlyMemory<byte> GetKey(ScopeContext scope, string typeName);
}
