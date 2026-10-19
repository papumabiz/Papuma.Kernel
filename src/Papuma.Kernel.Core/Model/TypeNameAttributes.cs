// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Model;

/// <summary>
/// Declares the logical name of a document type (ADR-028): the value stored in every row's
/// <c>document_type</c> column, used in key index names, <c>Reference</c> policy text, hash key
/// derivation, metrics and exports. Without it the name is the simple class name — which means
/// renaming or moving the class changes where its documents are looked for. Declare a name for any
/// type that must survive a refactoring or is shared with another implementation. The name must
/// match <c>[A-Za-z][A-Za-z0-9_]{1,100}</c>, and be unique among a model's document types
/// (compared case-insensitively). Fluent configuration (<c>Named</c>) overrides this.
/// </summary>
/// <param name="name">The logical document type name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class DocumentTypeAttribute(string name) : Attribute
{
    /// <summary>Gets the logical document type name.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// Declares the logical name of an event type (ADR-028) — the <c>event_type</c> of every stored
/// event. Same rules and reasons as <see cref="DocumentTypeAttribute"/>; event names are unique
/// among a model's event types, independently of its document types.
/// </summary>
/// <param name="name">The logical event type name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class EventTypeAttribute(string name) : Attribute
{
    /// <summary>Gets the logical event type name.</summary>
    public string Name { get; } = name;
}
