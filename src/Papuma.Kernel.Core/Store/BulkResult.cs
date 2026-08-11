// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// The result of a bulk operation (ADR-014).
/// </summary>
/// <param name="Count">The number of affected documents (one change record each).</param>
/// <param name="CorrelationId">The correlation id shared by all change records of this operation.</param>
public sealed record BulkResult(int Count, Guid CorrelationId);
