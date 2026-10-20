// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;

namespace Papuma.Kernel.Model;

/// <summary>
/// Central serializer configuration for document persistence.
/// </summary>
/// <remarks>
/// Web defaults: camelCase property names on write, case-insensitive matching on read.
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
    };
}
