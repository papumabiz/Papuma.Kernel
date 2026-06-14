// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Events;

/// <summary>
/// One entry of the event log as delivered to event handlers (ADR-013).
/// </summary>
/// <param name="Seq">The global event log sequence number (separate from the change feed).</param>
/// <param name="Scope">The scope the event belongs to.</param>
/// <param name="EventType">The logical event type name.</param>
/// <param name="Payload">The policy-applied payload (redacted fields are absent, hashed fields are hex strings).</param>
/// <param name="Metadata">Correlation/causation/actor metadata of the appending session.</param>
/// <param name="OccurredAt">When the event was recorded.</param>
public sealed record EventRecord(
    long Seq,
    ScopeContext Scope,
    string EventType,
    JsonObject Payload,
    JsonObject Metadata,
    DateTimeOffset OccurredAt)
{
    /// <summary>
    /// Deserializes the payload to the event CLR type. Redacted fields come back as
    /// their defaults, hashed fields as hex strings — consumers must not expect the
    /// original values of policy-protected fields (ADR-013).
    /// </summary>
    /// <typeparam name="T">The event CLR type.</typeparam>
    public T Deserialize<T>() where T : class =>
        Payload.Deserialize<T>(KernelJson.Options)
            ?? throw new InvalidOperationException($"Event payload of type {EventType} deserialized to null.");
}
