// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Store;

/// <summary>
/// The result of a successful write: new version, operation, and the policy-applied diff.
/// </summary>
/// <param name="Version">The document version after the write.</param>
/// <param name="Operation">The recorded change operation.</param>
/// <param name="Diff">The reversible field diff recorded in the change feed (ADR-004).</param>
public sealed record SaveResult(long Version, ChangeOperation Operation, DocumentDiff Diff);
