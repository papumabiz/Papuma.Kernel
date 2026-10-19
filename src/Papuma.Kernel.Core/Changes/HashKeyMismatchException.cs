// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Changes;

/// <summary>
/// Thrown at startup when this instance's hash key does not match the key the database was
/// initialized with for the same key id (ADR-025): the master key or the key id is misconfigured
/// on this instance, or a secret was swapped. Starting anyway would write hashes that match
/// nothing written by the other instances. Neither the key nor the stored check value is ever
/// part of the message.
/// </summary>
public sealed class HashKeyMismatchException : Exception
{
    /// <summary>Gets the key id whose check value differs.</summary>
    public string KeyId { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HashKeyMismatchException"/> class.
    /// </summary>
    /// <param name="keyId">The key id whose check value differs.</param>
    public HashKeyMismatchException(string keyId)
        : base(
            $"The hash key '{keyId}' configured for this instance is not the key this database was initialized " +
            "with under that id. Configure the same master key everywhere, or — to rotate — configure a new " +
            "HashKeyId together with the new key (ADR-025).")
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        KeyId = keyId;
    }
}
