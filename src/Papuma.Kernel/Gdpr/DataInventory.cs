// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Reflection;

using Papuma.Kernel.Changes;
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
    /// nested POCOs are traversed, the members of every <c>[JsonDerivedType]</c> subtype
    /// are listed under the property's path (review N-14), policies inherit from the
    /// nearest declared ancestor path (ADR-007).
    /// </summary>
    private static List<FieldInventory> EnumerateFields(
        Type clrType, IReadOnlyDictionary<string, FieldPolicy> policies)
    {
        var fields = new Dictionary<string, FieldInventory>(StringComparer.Ordinal);
        Walk(clrType, prefix: null, policies, fields, visited: []);
        return fields.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static void Walk(
        Type type,
        string? prefix,
        IReadOnlyDictionary<string, FieldPolicy> policies,
        Dictionary<string, FieldInventory> fields,
        HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return; // cycle guard
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (JsonPathResolver.IsIgnored(property))
            {
                continue; // not serialized → not in the inventory (review L7)
            }

            var jsonName = JsonPathResolver.JsonNameOf(property);
            var path = DiffPath.Append(prefix, jsonName);

            // The same view as the metamodel scan (ModelTypeShape): classes and structs are traversed, so the
            // inventory shows the paths a policy on a struct member protects (review C-1, S-20).
            if (ModelTypeShape.DerivedTypesOf(property.PropertyType) is { Count: > 0 } derived)
            {
                // Subtypes share the property's path; a member two of them declare is listed once.
                var declared = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (!declared.IsInterface && !declared.IsAbstract)
                {
                    Walk(declared, path, policies, fields, visited);
                }

                foreach (var subtype in derived)
                {
                    Walk(subtype, path, policies, fields, visited);
                }
            }
            else if (ModelTypeShape.NestedTypeOf(property.PropertyType) is { } nested)
            {
                Walk(nested, path, policies, fields, visited);
            }
            else
            {
                fields[path] = new FieldInventory(path, ResolvePolicy(policies, path));
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

            if (!DiffPath.TryGetParent(current, out var parent))
            {
                return FieldPolicy.Track;
            }

            current = parent;
        }
    }

}
