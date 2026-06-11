// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json;

namespace Papuma.Kernel.Store;

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
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
