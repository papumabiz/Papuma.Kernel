// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Collections;
using System.Reflection;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Builds the Art.-30 data inventory from the startup metamodel (ADR-015). Pure
/// metadata — no database access; the result describes what the model <em>would</em>
/// store and track, independent of actual data.
/// </summary>
public static class DataInventory
{
    /// <summary>
    /// Builds the inventory report over all registered document and event types.
    /// </summary>
    /// <param name="model">The startup-built kernel model.</param>
    public static DataInventoryReport Build(KernelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var documents = model.DocumentTypes
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => new DocumentInventory(
                m.Name,
                m.ClrType.FullName ?? m.ClrType.Name,
                m.SchemaVersion,
                EnumerateFields(m.ClrType, m.Policies),
                m.Keys.Where(k => k.Unique).Select(k => k.Path).ToList(),
                m.Keys.Where(k => !k.Unique).Select(k => k.Path).ToList()))
            .ToList();

        var events = model.EventTypes
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => new EventInventory(
                m.Name,
                m.ClrType.FullName ?? m.ClrType.Name,
                EnumerateFields(m.ClrType, m.Policies),
                m.Retention))
            .ToList();

        return new DataInventoryReport(documents, events);
    }

    /// <summary>
    /// Enumerates all leaf JSON paths of a CLR type with their effective policy.
    /// Mirrors the diff engine's view: collections are atomic leaves (ADR-004),
    /// nested POCOs are traversed, policies inherit from the nearest declared
    /// ancestor path (ADR-007).
    /// </summary>
    private static List<FieldInventory> EnumerateFields(
        Type clrType, IReadOnlyDictionary<string, FieldPolicy> policies)
    {
        var fields = new List<FieldInventory>();
        Walk(clrType, prefix: string.Empty, policies, fields, visited: []);
        fields.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return fields;
    }

    private static void Walk(
        Type type,
        string prefix,
        IReadOnlyDictionary<string, FieldPolicy> policies,
        List<FieldInventory> fields,
        HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return; // cycle guard
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var jsonName = JsonPathResolver.JsonNameOf(property);
            var path = prefix.Length == 0 ? jsonName : $"{prefix}.{jsonName}";

            if (IsNestedPocoType(property.PropertyType))
            {
                Walk(property.PropertyType, path, policies, fields, visited);
            }
            else
            {
                fields.Add(new FieldInventory(path, ResolvePolicy(policies, path)));
            }
        }

        visited.Remove(type);
    }

    /// <summary>
    /// Resolves the effective policy: exact path first, then the nearest declared
    /// ancestor — same semantics as <see cref="DocumentTypeMetadata.ResolvePolicy"/>.
    /// </summary>
    private static FieldPolicy ResolvePolicy(
        IReadOnlyDictionary<string, FieldPolicy> policies, string path)
    {
        var current = path;
        while (true)
        {
            if (policies.TryGetValue(current, out var policy))
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

    private static bool IsNestedPocoType(Type type)
    {
        if (!type.IsClass || type == typeof(string))
        {
            return false;
        }

        // Collections are atomic diff values (ADR-004) — they appear as one leaf path.
        return !typeof(IEnumerable).IsAssignableFrom(type);
    }
}
