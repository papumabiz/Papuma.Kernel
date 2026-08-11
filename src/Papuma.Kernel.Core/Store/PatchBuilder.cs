// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Store;

/// <summary>
/// Collects the operations of a partial update (ADR-012). The catalog is deliberately
/// minimal — <see cref="Set{TValue}"/>, <see cref="Remove"/>, <see cref="Increment"/> —
/// anything beyond it is Load + Save, not catalog growth.
/// </summary>
/// <typeparam name="T">The document CLR type.</typeparam>
public sealed class PatchBuilder<T> where T : class
{
    internal List<PatchOperation> Operations { get; } = [];

    internal PatchBuilder()
    {
    }

    /// <summary>
    /// Sets a field to a value. Parent objects along the path must already exist
    /// in the stored document.
    /// </summary>
    public PatchBuilder<T> Set<TValue>(Expression<Func<T, TValue>> property, TValue value)
    {
        ArgumentNullException.ThrowIfNull(property);

        var path = JsonPathResolver.Resolve(property).Split('.');
        var node = JsonSerializer.SerializeToNode(value, KernelJson.Options);
        Operations.Add(new PatchOperation(PatchOperationKind.Set, path, node, IncrementBy: 0));
        return this;
    }

    /// <summary>
    /// Removes a field from the document.
    /// </summary>
    public PatchBuilder<T> Remove(Expression<Func<T, object?>> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        var path = JsonPathResolver.Resolve(property).Split('.');
        Operations.Add(new PatchOperation(PatchOperationKind.Remove, path, Value: null, IncrementBy: 0));
        return this;
    }

    /// <summary>
    /// Atomically increments a numeric field by <paramref name="by"/> (missing or null
    /// fields count as 0). The read-modify-write happens inside the statement — no
    /// conflict window, no <c>expectedVersion</c> needed (ADR-012).
    /// </summary>
    public PatchBuilder<T> Increment(Expression<Func<T, object?>> property, long by = 1)
    {
        ArgumentNullException.ThrowIfNull(property);

        var path = JsonPathResolver.Resolve(property).Split('.');
        Operations.Add(new PatchOperation(PatchOperationKind.Increment, path, Value: null, IncrementBy: by));
        return this;
    }
}

internal enum PatchOperationKind
{
    Set,
    Remove,
    Increment,
}

internal sealed record PatchOperation(
    PatchOperationKind Kind,
    string[] PathSegments,
    JsonNode? Value,
    long IncrementBy);
