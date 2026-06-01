// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Represents the stored history for one entity across change feed and business events.
/// </summary>
public sealed record EntityHistory(
    List<ChangeRecord> ChangeRecords,
    List<BusinessEventRecord> BusinessEvents);