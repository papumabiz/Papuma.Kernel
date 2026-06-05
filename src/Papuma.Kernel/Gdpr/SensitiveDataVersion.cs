// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Represents one immutable version entry in the sensitive data store.
/// </summary>
public sealed record SensitiveDataVersion(
    SensitiveRef SensitiveRef,
    int Version,
    int SchemaVersion,
    string PayloadJson,
    SensitiveDataState State,
    bool LegalHold,
    string ActorId,
    DateTimeOffset CreatedAt,
    string? Reason,
    ScopeContext Scope);