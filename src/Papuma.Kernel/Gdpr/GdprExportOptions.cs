// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Options for <see cref="GdprExport"/>.
/// </summary>
public sealed class GdprExportOptions
{
    /// <summary>
    /// Gets a value indicating whether the change and event metadata is exported in full,
    /// including the actor (<c>actorId</c>), the causation (<c>causationId</c>,
    /// <c>causationType</c>), the trace context (<c>traceparent</c>) and the free audit text of a
    /// redaction (<c>redaction.reason</c>, <c>redaction.actorId</c>). The default is <c>false</c>:
    /// those fields name or describe other people (clerks, requesters) and are not part of the
    /// data subject's right of access (Art. 15 (4) GDPR) — only the correlation id, rollback
    /// markers and the time and paths of a redaction are exported. Opt in for an internal audit
    /// export that is not handed to the data subject.
    /// </summary>
    public bool IncludeActorMetadata { get; init; }
}
