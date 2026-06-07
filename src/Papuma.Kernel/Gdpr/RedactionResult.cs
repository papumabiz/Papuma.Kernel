// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Captures how many entries were redacted during a GDPR redaction request.
/// </summary>
public sealed record RedactionResult(int EventsRedacted);
