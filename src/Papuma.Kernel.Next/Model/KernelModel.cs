// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Model;

/// <summary>
/// The immutable, startup-built metamodel over all registered document types
/// (architecture §6). Build via <see cref="KernelModelBuilder"/>.
/// </summary>
public sealed class KernelModel
{
    private readonly IReadOnlyDictionary<Type, DocumentTypeMetadata> _byType;
    private readonly IReadOnlyDictionary<Type, EventTypeMetadata> _eventsByType;
    private readonly IReadOnlyDictionary<string, (DocumentTypeMetadata Metadata, KeyMetadata Key)> _byIndexName;

    internal KernelModel(
        IReadOnlyDictionary<Type, DocumentTypeMetadata> byType,
        IReadOnlyDictionary<Type, EventTypeMetadata> eventsByType)
    {
        _byType = byType;
        _eventsByType = eventsByType;

        var byIndexName = new Dictionary<string, (DocumentTypeMetadata, KeyMetadata)>(StringComparer.Ordinal);
        foreach (var metadata in byType.Values)
        {
            foreach (var key in metadata.Keys)
            {
                byIndexName[key.IndexName] = (metadata, key);
            }
        }

        _byIndexName = byIndexName;
    }

    /// <summary>Gets the metadata of all registered document types.</summary>
    public IReadOnlyCollection<DocumentTypeMetadata> DocumentTypes => (IReadOnlyCollection<DocumentTypeMetadata>)_byType.Values;

    /// <summary>Gets the metadata of all registered event types (ADR-013).</summary>
    public IReadOnlyCollection<EventTypeMetadata> EventTypes => (IReadOnlyCollection<EventTypeMetadata>)_eventsByType.Values;

    /// <summary>
    /// Returns the metadata for an event CLR type.
    /// </summary>
    /// <typeparam name="T">The event CLR type.</typeparam>
    /// <exception cref="EventTypeNotRegisteredException">The type is not registered.</exception>
    public EventTypeMetadata GetRequiredEvent<T>() where T : class =>
        _eventsByType.TryGetValue(typeof(T), out var metadata)
            ? metadata
            : throw new EventTypeNotRegisteredException(typeof(T));

    /// <summary>
    /// Returns the metadata for a CLR type.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <exception cref="DocumentTypeNotRegisteredException">The type is not registered.</exception>
    public DocumentTypeMetadata GetRequired<T>() => GetRequired(typeof(T));

    /// <summary>
    /// Returns the metadata for a CLR type.
    /// </summary>
    /// <param name="clrType">The document CLR type.</param>
    /// <exception cref="DocumentTypeNotRegisteredException">The type is not registered.</exception>
    public DocumentTypeMetadata GetRequired(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);

        return _byType.TryGetValue(clrType, out var metadata)
            ? metadata
            : throw new DocumentTypeNotRegisteredException(clrType);
    }

    /// <summary>
    /// Maps a PostgreSQL constraint/index name from a unique violation (23505) back to
    /// the declared key, or <c>null</c> when the constraint is not a declared key index.
    /// </summary>
    /// <param name="constraintName">The constraint name reported by PostgreSQL.</param>
    public (DocumentTypeMetadata Metadata, KeyMetadata Key)? FindKeyByIndexName(string? constraintName) =>
        constraintName is not null && _byIndexName.TryGetValue(constraintName, out var match)
            ? match
            : null;
}
