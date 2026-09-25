// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;

namespace Papuma.Kernel.Store;

/// <summary>
/// The result of a successful write: new version, operation, and the policy-applied diff.
/// </summary>
/// <param name="Version">The document version after the write.</param>
/// <param name="Operation">The recorded change operation.</param>
/// <param name="Diff">The reversible field diff recorded in the change feed (ADR-004).</param>
public sealed record SaveResult(long Version, ChangeOperation Operation, DocumentDiff Diff)
{
    /// <summary>The persisted state after the write and its CLR type; <c>null</c> after a delete.</summary>
    internal (JsonObject Data, Type ClrType)? Stored { get; init; }

    /// <summary>
    /// Returns the document as persisted by this write — e.g. the value an
    /// <c>Increment</c> produced, without a second read and without the conflict window
    /// of load + save. Unlike <see cref="Diff"/>, it is not policy-applied and contains
    /// every field; it is what <c>LoadAsync</c> would return right after the write.
    /// Each call returns a fresh instance.
    /// </summary>
    /// <typeparam name="T">The document CLR type the write was made with.</typeparam>
    /// <exception cref="InvalidOperationException">
    /// The write was a delete (no document remains), the result was not produced by a
    /// session write, or <typeparamref name="T"/> is not the written document type.
    /// </exception>
    public T GetDocument<T>() where T : class
    {
        if (Stored is not { } stored)
        {
            throw new InvalidOperationException(Operation == ChangeOperation.Delete
                ? "A delete leaves no document to return."
                : "This result carries no persisted document; only results of session writes do.");
        }

        if (stored.ClrType != typeof(T))
        {
            throw new InvalidOperationException(
                $"The write stored a {stored.ClrType.Name}, not a {typeof(T).Name}.");
        }

        return stored.Data.Deserialize<T>(KernelJson.Options)
            ?? throw new InvalidOperationException($"Document of type {typeof(T).Name} deserialized to null.");
    }
}
