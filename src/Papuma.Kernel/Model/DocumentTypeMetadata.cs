// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

namespace Papuma.Kernel.Model;

/// <summary>
/// The startup-built metadata of one registered document type: name, id access,
/// field policies, declared keys, and the upcaster chain (architecture §6).
/// </summary>
public sealed class DocumentTypeMetadata
{
    private readonly Func<object, string?> _idGetter;
    private readonly IReadOnlyList<Action<JsonObject>> _upcasters;

    /// <summary>
    /// Gets the optional document validator (throws to reject). Patch paths deserialize
    /// the stored result and run it before commit (ADR-012, point 5).
    /// </summary>
    public Action<object>? Validator { get; }

    /// <summary>Gets the logical document type name (table column <c>document_type</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>
    /// Gets the current schema version of the type: highest registered upcaster
    /// <c>fromVersion</c> + 1; 1 when no upcasters are registered (ADR-005).
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Gets non-default field policies keyed by dot-separated JSON path.
    /// Paths not present default to <see cref="FieldPolicy.Track"/>.
    /// </summary>
    public IReadOnlyDictionary<string, FieldPolicy> Policies { get; }

    /// <summary>Gets the declared keys.</summary>
    public IReadOnlyList<KeyMetadata> Keys { get; }

    /// <summary>
    /// Gets whether this type may be read over the MCP content tools as a
    /// policy-projected (masked) document (ADR-016). Default <c>false</c> — exposure is
    /// opt-in via <c>ExposeToMcp()</c>; the safe default is "not exposed".
    /// </summary>
    public bool ExposedToMcp { get; }

    internal DocumentTypeMetadata(
        string name,
        Type clrType,
        IReadOnlyDictionary<string, FieldPolicy> policies,
        IReadOnlyList<KeyMetadata> keys,
        Func<object, string?> idGetter,
        IReadOnlyList<Action<JsonObject>> upcasters,
        Action<object>? validator,
        bool exposedToMcp)
    {
        Name = name;
        ClrType = clrType;
        Policies = policies;
        Keys = keys;
        _idGetter = idGetter;
        _upcasters = upcasters;
        Validator = validator;
        ExposedToMcp = exposedToMcp;
        SchemaVersion = upcasters.Count + 1;
    }

    /// <summary>
    /// Runs the upcaster chain on a raw document, lifting it from
    /// <paramref name="storedSchemaVersion"/> to the current <see cref="SchemaVersion"/>.
    /// Mutates <paramref name="json"/> in place.
    /// </summary>
    /// <param name="json">The raw stored document.</param>
    /// <param name="storedSchemaVersion">The schema version the document was stored with.</param>
    public void Upcast(JsonObject json, int storedSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(json);

        // Upcaster at index v-1 lifts version v → v+1 (chain is validated contiguous at build).
        for (var version = storedSchemaVersion; version < SchemaVersion; version++)
        {
            _upcasters[version - 1](json);
        }
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
