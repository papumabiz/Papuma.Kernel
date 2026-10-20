// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;

namespace Papuma.Kernel.Store;

/// <summary>Checks shared by both kernels' patch paths, before any SQL is built.</summary>
internal static class PatchGuards
{
    /// <summary>
    /// Refuses a patch that sets, removes or increments the property carrying the document id. The id lives twice —
    /// in the <c>id</c> column and in the document — and a patch only changes the document: <c>LoadAsync(id)</c>
    /// would return a document with a foreign id, the next save would address another document, and feed
    /// references would name the wrong one (review S-1).
    /// </summary>
    /// <param name="operations">The collected operations.</param>
    /// <param name="metadata">The document type.</param>
    /// <param name="parameterName">The parameter to blame — the patch delegate.</param>
    /// <exception cref="ArgumentException">An operation targets the id property.</exception>
    public static void EnsureNoOperationOnTheId(
        IEnumerable<PatchOperation> operations, DocumentTypeMetadata metadata, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(metadata);

        foreach (var operation in operations)
        {
            if (string.Equals(DiffPath.Join(operation.PathSegments), metadata.IdPath, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"A patch cannot {operation.Kind.ToString().ToLowerInvariant()} the id property '{metadata.IdPath}' of " +
                    $"{metadata.Name}: the id is the document's identity, kept in the id column as well, and a patch " +
                    "would let the two drift apart. Save a new document under the new id instead.",
                    parameterName);
            }
        }
    }
}
