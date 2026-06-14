// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Store;

/// <summary>
/// The result of a successful write: new version, operation, and the policy-applied diff.
/// </summary>
/// <param name="Version">The document version after the write.</param>
/// <param name="Operation">The recorded change operation.</param>
/// <param name="Diff">The reversible field diff recorded in the change feed (ADR-004).</param>
public sealed record SaveResult(long Version, ChangeOperation Operation, DocumentDiff Diff);
