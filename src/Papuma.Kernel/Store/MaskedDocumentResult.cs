// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Store;

/// <summary>
/// The result of a policy-projected read (ADR-016): the document as JSON with field
/// policies applied (sensitive fields masked, hashed or omitted), plus its version.
/// Deliberately not a typed <c>T</c> — masking can replace a typed value with a marker
/// string, so the masked shape is JSON, not a domain object.
/// </summary>
/// <param name="Document">The masked document JSON.</param>
/// <param name="Version">The document version (optimistic concurrency token).</param>
public sealed record MaskedDocumentResult(JsonObject Document, long Version);
