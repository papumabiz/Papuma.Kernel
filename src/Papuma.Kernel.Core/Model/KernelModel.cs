// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
    /// Fails when this model declares a <see cref="FieldPolicy.Hash"/> policy but no hash key
    /// provider is configured (ADR-025): the kernel has no unkeyed hash, so it refuses to start
    /// rather than write a hash it cannot compute — or worse, a weak one.
    /// </summary>
    /// <param name="hashKeys">The configured provider, or <c>null</c>.</param>
    /// <exception cref="InvalidOperationException">A Hash policy is declared and <paramref name="hashKeys"/> is <c>null</c>.</exception>
    internal void EnsureHashKeysConfigured(Changes.IHashKeyProvider? hashKeys)
    {
        if (hashKeys is not null)
        {
            return;
        }

        var offenders = new List<string>();
        foreach (var type in _byType.Values)
        {
            offenders.AddRange(type.Policies.Where(p => p.Value == FieldPolicy.Hash).Select(p => $"{type.Name}.{p.Key} (document)"));
        }

        foreach (var type in _eventsByType.Values)
        {
            offenders.AddRange(type.Policies.Where(p => p.Value == FieldPolicy.Hash).Select(p => $"{type.Name}.{p.Key} (event)"));
        }

        if (offenders.Count > 0)
        {
            offenders.Sort(StringComparer.Ordinal);
            throw new InvalidOperationException(
                "The model declares Hash policies (" + string.Join(", ", offenders) + ") but no hash key is configured. " +
                "The Hash policy is keyed (ADR-025): set PapumaKernelOptions.HashMasterKey (base64, at least 32 random bytes, " +
                "e.g. `openssl rand -base64 32`) and HashKeyId, or register an IHashKeyProvider. " +
                "In tests use Papuma.Kernel.Testing.TestHashKeys.");
        }
    }

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
    /// Maps a storage layer's constraint/index name from a unique-violation error back to
    /// the declared key, or <c>null</c> when the constraint is not a declared key index.
    /// </summary>
    /// <param name="constraintName">The constraint name reported by the storage layer.</param>
    public (DocumentTypeMetadata Metadata, KeyMetadata Key)? FindKeyByIndexName(string? constraintName) =>
        constraintName is not null && _byIndexName.TryGetValue(constraintName, out var match)
            ? match
            : null;
}
