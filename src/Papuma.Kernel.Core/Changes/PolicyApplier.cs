// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Applies field policies (ADR-007) to a raw diff before it reaches the change feed.
/// This runs inside the write transaction — no code path writes an unpoliced diff.
/// </summary>
internal static class PolicyApplier
{
    /// <summary>
    /// Transforms a raw tracked diff into its policy-applied form.
    /// </summary>
    /// <param name="diff">The raw diff produced by <see cref="JsonDiffEngine"/>.</param>
    /// <param name="metadata">The document type metadata carrying the policies.</param>
    /// <param name="documentId">The document id (for reference entries).</param>
    /// <param name="hashKeys">The hash key provider, or <c>null</c> for the legacy v1 hash (ADR-025).</param>
    /// <param name="scope">The scope of the write — the key of a keyed hash is derived per tenant.</param>
    public static DocumentDiff Apply(
        DocumentDiff diff,
        DocumentTypeMetadata metadata,
        string documentId,
        IHashKeyProvider? hashKeys,
        ScopeContext scope)
    {
        if (metadata.Policies.Count == 0 || diff.IsEmpty)
        {
            return diff;
        }

        var hash = new HashContext(hashKeys, scope, metadata.Name);
        var entries = new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal);
        foreach (var (path, entry) in diff.Entries)
        {
            switch (metadata.ResolvePolicy(path))
            {
                case FieldPolicy.Track:
                    entries[path] = entry;
                    break;

                case FieldPolicy.Redact:
                    entries[path] = DiffEntry.RedactedEntry();
                    break;

                case FieldPolicy.Reference:
                    entries[path] = DiffEntry.ReferenceEntry($"{metadata.Name}/{documentId}/{path}");
                    break;

                case FieldPolicy.Hash:
                    entries[path] = DiffEntry.HashedEntry(HashOf(entry, hash));
                    break;

                case FieldPolicy.DoNotTrack:
                    break; // field never reaches the feed
            }
        }

        return new DocumentDiff(entries);
    }

    private static string? HashOf(DiffEntry entry, HashContext hash) =>
        entry.HasNew ? hash.Hash(entry.New) : null; // field removed → nothing to hash
}
