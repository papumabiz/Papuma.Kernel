// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Model;

/// <summary>
/// The startup-built metadata of one registered document type: name, id access,
/// field policies, and declared keys (architecture §6).
/// </summary>
public sealed class DocumentTypeMetadata
{
    private readonly Func<object, string?> _idGetter;

    /// <summary>Gets the logical document type name (table column <c>document_type</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>Gets the current schema version of the type (fixed 1 until phase 4).</summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Gets non-default field policies keyed by dot-separated JSON path.
    /// Paths not present default to <see cref="FieldPolicy.Track"/>.
    /// </summary>
    public IReadOnlyDictionary<string, FieldPolicy> Policies { get; }

    /// <summary>Gets the declared keys.</summary>
    public IReadOnlyList<KeyMetadata> Keys { get; }

    internal DocumentTypeMetadata(
        string name,
        Type clrType,
        int schemaVersion,
        IReadOnlyDictionary<string, FieldPolicy> policies,
        IReadOnlyList<KeyMetadata> keys,
        Func<object, string?> idGetter)
    {
        Name = name;
        ClrType = clrType;
        SchemaVersion = schemaVersion;
        Policies = policies;
        Keys = keys;
        _idGetter = idGetter;
    }

    /// <summary>
    /// Extracts the document id from a document instance.
    /// </summary>
    /// <param name="document">The document instance.</param>
    /// <exception cref="InvalidOperationException">Thrown when the id is null or empty.</exception>
    public string GetDocumentId(object document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var id = _idGetter(document);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                $"Document of type {Name} has a null or empty id. Populate the id before saving.");
        }

        return id;
    }

    /// <summary>
    /// Resolves the effective policy for a diff path: exact match first, then the
    /// nearest declared ancestor path (a policy on <c>address</c> covers <c>address.city</c>).
    /// </summary>
    /// <param name="path">The dot-separated diff path.</param>
    public FieldPolicy ResolvePolicy(string path)
    {
        var current = path;
        while (true)
        {
            if (Policies.TryGetValue(current, out var policy))
            {
                return policy;
            }

            var lastDot = current.LastIndexOf('.');
            if (lastDot < 0)
            {
                return FieldPolicy.Track;
            }

            current = current[..lastDot];
        }
    }
}
