// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Store;

/// <summary>
/// The in-process definition of patch semantics (<c>Set</c>/<c>Remove</c>/<c>Increment</c>,
/// <c>docs/spec/patch.md</c>). The SQLite kernel executes patches with it; the PostgreSQL
/// kernel executes them as one nested <c>jsonb_set</c>/<c>#-</c> SQL expression
/// (<c>BuildPatchExpression</c>) and uses <see cref="EnsureTargetsExist"/> to apply the
/// rules SQL cannot express.
/// </summary>
/// <remarks>
/// Rules, pinned by the shared vectors in <c>docs/spec/vectors/patch.json</c>: operations
/// apply in order, each seeing the previous result; <c>Set</c>/<c>Increment</c> below a
/// missing or non-object parent throw <see cref="PatchTargetMissingException"/> (parents are
/// never created or replaced); <c>Increment</c> on a value that is not absent, null or a
/// number throws <see cref="NonNumericIncrementException"/> instead of overwriting it;
/// <c>Remove</c> of something absent is a no-op.
/// </remarks>
internal static class JsonPatchApplier
{
    /// <summary>Applies <paramref name="operations"/> to <paramref name="target"/> in place.</summary>
    public static void Apply(
        JsonObject target,
        IReadOnlyList<PatchOperation> operations,
        string documentType,
        string? documentId) =>
        ApplyCore(target, operations, documentType, documentId, strictNumbers: true);

    /// <summary>
    /// Checks that every <c>Set</c>/<c>Increment</c> of <paramref name="operations"/> reaches
    /// its parent when applied in order to <paramref name="before"/> (not modified). For an
    /// engine that already executed the patch in SQL: numeric typing was enforced there, so it
    /// is not re-checked here.
    /// </summary>
    public static void EnsureTargetsExist(
        JsonObject before,
        IReadOnlyList<PatchOperation> operations,
        string documentType,
        string? documentId) =>
        ApplyCore((JsonObject)before.DeepClone(), operations, documentType, documentId, strictNumbers: false);

    private static void ApplyCore(
        JsonObject target,
        IReadOnlyList<PatchOperation> operations,
        string documentType,
        string? documentId,
        bool strictNumbers)
    {
        foreach (var op in operations)
        {
            switch (op.Kind)
            {
                case PatchOperationKind.Set:
                    RequireParent(target, op, documentType, documentId)[op.PathSegments[^1]] = op.Value?.DeepClone();
                    break;

                case PatchOperationKind.Remove:
                    NavigateToParent(target, op.PathSegments)?.Remove(op.PathSegments[^1]);
                    break;

                case PatchOperationKind.Increment:
                    Increment(RequireParent(target, op, documentType, documentId), op, documentType, documentId, strictNumbers);
                    break;
            }
        }
    }

    private static void Increment(
        JsonObject parent,
        PatchOperation op,
        string documentType,
        string? documentId,
        bool strictNumbers)
    {
        var key = op.PathSegments[^1];
        var current = 0m;
        if (parent[key] is { } existing
            && !(existing is JsonValue value
                 && value.GetValueKind() == JsonValueKind.Number
                 && decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out current)))
        {
            // A JSON number only: strings, booleans, objects and arrays are never coerced.
            if (strictNumbers)
            {
                throw new NonNumericIncrementException(documentType, documentId, string.Join('.', op.PathSegments));
            }

            current = 0m;
        }

        parent[key] = JsonValue.Create(current + op.IncrementBy);
    }

    private static JsonObject RequireParent(
        JsonObject root,
        PatchOperation op,
        string documentType,
        string? documentId) =>
        NavigateToParent(root, op.PathSegments)
        ?? throw new PatchTargetMissingException(documentType, documentId, string.Join('.', op.PathSegments));

    /// <summary>
    /// Walks to the parent object of the last path segment, or returns <c>null</c> as soon as
    /// the path cannot be followed through existing objects.
    /// </summary>
    private static JsonObject? NavigateToParent(JsonObject root, IReadOnlyList<string> segments)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
