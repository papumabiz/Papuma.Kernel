// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Security.Cryptography;
using System.Text;

using Papuma.Kernel.Model;

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
    public static DocumentDiff Apply(DocumentDiff diff, DocumentTypeMetadata metadata, string documentId)
    {
        if (metadata.Policies.Count == 0 || diff.IsEmpty)
        {
            return diff;
        }

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
                    entries[path] = DiffEntry.HashedEntry(HashOf(entry));
                    break;

                case FieldPolicy.DoNotTrack:
                    break; // field never reaches the feed
            }
        }

        return new DocumentDiff(entries);
    }

    private static string? HashOf(DiffEntry entry)
    {
        if (!entry.HasNew)
        {
            return null; // field removed — nothing to hash
        }

        var text = entry.New?.ToJsonString() ?? "null";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
