// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Changes;

/// <summary>
/// The technical change operation recorded in the change feed (ADR-002).
/// Rollbacks are updates with metadata, never a fourth operation (ADR-008).
/// </summary>
public enum ChangeOperation : short
{
    /// <summary>The document was created.</summary>
    Insert = 1,

    /// <summary>The document was updated (including patches and rollbacks).</summary>
    Update = 2,

    /// <summary>The document was deleted.</summary>
    Delete = 3,
}
