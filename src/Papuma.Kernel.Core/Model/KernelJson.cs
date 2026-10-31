// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Papuma.Kernel.Model;

/// <summary>
/// Central serializer configuration for document persistence.
/// </summary>
/// <remarks>
/// Web defaults: camelCase property names on write, case-insensitive matching on read. Enum values are
/// written by member name (as declared, not camelCased) and read from a name or, for documents written before
/// 3.0, from a number.
/// Deliberately one fixed configuration — the serialized form is the document's wire
/// truth, so it must not vary between call sites.
/// </remarks>
internal static class KernelJson
{
    // One more than the engines' limit on purpose: the serializer writes at most MaxDepth - 1 levels (measured on
    // .NET 10), the parser reads MaxDepth. With 65 a document of exactly JsonLimits.MaxDepth (64) levels can be
    // saved, and the 65th level is refused — the same document the engines and the parser accept.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = Changes.JsonLimits.MaxDepth + 1,

        // jsonb stores object keys in its own order (shorter keys first), so the "$type" discriminator of a
        // [JsonDerivedType] value is no longer the first property when PostgreSQL hands the document back. Without
        // this the read fails with "must specify a type discriminator".
        AllowOutOfOrderMetadataProperties = true,

        // By name, not by number: a number made the feed unreadable, IsFieldTransition("status", "Pending", "Paid")
        // never matched, and a reordered enum silently changed every stored value. Numbers still read (old documents).
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: true) },
    };
}
