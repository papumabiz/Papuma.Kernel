// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;

namespace Papuma.Kernel.Store;

/// <summary>
/// The sessions' document serialization and parsing, so that "nested too deep" surfaces as one
/// <see cref="DocumentTooDeepException"/> on every write and read path instead of whatever the serializer or
/// parser happens to throw (a serializer <see cref="JsonException"/> that speaks of a possible cycle, a parser
/// <see cref="JsonException"/> with a byte position).
/// </summary>
internal static class DocumentJson
{
    /// <summary>Serializes a document or a value written into one with <see cref="KernelJson.Options"/>.</summary>
    public static JsonNode? Serialize<T>(T value, string? documentType, string? documentId, string detail = "cannot be stored")
    {
        try
        {
            return JsonSerializer.SerializeToNode(value, KernelJson.Options);
        }
        catch (JsonException ex) when (JsonLimits.IsSerializerDepthError(ex))
        {
            throw new DocumentTooDeepException(documentType, documentId, detail, ex);
        }
    }

    /// <summary>Parses a stored document state (as the store returns it) into a node tree.</summary>
    public static JsonObject ParseStored(string json, string documentType, string documentId, string detail = "cannot be read")
    {
        try
        {
            return (JsonObject)JsonNode.Parse(json)!;
        }
        catch (JsonException ex) when (JsonLimits.TextExceedsMaxDepth(json))
        {
            throw new DocumentTooDeepException(documentType, documentId, detail, ex);
        }
    }

    /// <summary>Throws when a document built in memory (a patched state) exceeds the limit.</summary>
    public static void EnsureDepth(JsonObject document, string documentType, string documentId, string detail)
    {
        if (JsonLimits.ExceedsMaxDepth(document))
        {
            throw new DocumentTooDeepException(documentType, documentId, detail);
        }
    }
}
