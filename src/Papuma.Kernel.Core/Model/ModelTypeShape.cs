// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections;
using System.Reflection;

namespace Papuma.Kernel.Model;

/// <summary>
/// How the metamodel scan and the data inventory see a property's type: a nested object whose
/// properties carry their own policies (a class or a user-defined struct, also as <c>Nullable&lt;T&gt;</c>),
/// a collection whose elements are tracked as one atomic value (ADR-004), or a leaf. One definition for both —
/// two copies drifted apart before (review C-1, S-20).
/// </summary>
internal static class ModelTypeShape
{
    /// <summary>
    /// Returns the type to descend into for a property of <paramref name="propertyType"/>, or <c>null</c>
    /// for a leaf (primitive, string, enum, framework value type, collection). A class descends as it always
    /// did; a user-defined struct and <c>Nullable&lt;struct&gt;</c> descend the same way — the serializer writes
    /// them as JSON objects, so a policy attribute on one of their members has a path (review C-1).
    /// </summary>
    /// <param name="propertyType">The declared type of the property.</param>
    public static Type? NestedTypeOf(Type propertyType)
    {
        ArgumentNullException.ThrowIfNull(propertyType);

        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return null; // collections and dictionaries are atomic diff values (ADR-004)
        }

        if (type.IsClass)
        {
            // A string is a leaf; so is a JsonNode property — free-form JSON, not members of ours.
            return type == typeof(string) || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type) ? null : type;
        }

        return type.IsValueType && IsObjectShaped(type) ? type : null;
    }

    /// <summary>
    /// Returns the element type of a collection property (<c>List&lt;T&gt;</c>, <c>T[]</c>, <c>IEnumerable&lt;T&gt;</c>;
    /// the value type of a dictionary), or <c>null</c> when the property is not a generic collection.
    /// </summary>
    /// <param name="propertyType">The declared type of the property.</param>
    public static Type? ElementTypeOf(Type propertyType)
    {
        ArgumentNullException.ThrowIfNull(propertyType);

        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        // A dictionary also implements IEnumerable<KeyValuePair<,>>: ask for its value type first.
        return GenericArgument(type, typeof(IDictionary<,>), 1)
            ?? GenericArgument(type, typeof(IReadOnlyDictionary<,>), 1)
            ?? GenericArgument(type, typeof(IEnumerable<>), 0);
    }

    /// <summary>
    /// Whether the serializer writes a value of this type as a JSON object with the type's own properties:
    /// not a primitive, string, enum or <c>decimal</c>, and not a framework type (<c>DateTimeOffset</c>,
    /// <c>Guid</c>, <c>JsonNode</c> …), whose properties are never ours to protect.
    /// </summary>
    /// <param name="type">The type.</param>
    public static bool IsObjectShaped(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return (type.IsClass || type.IsValueType)
            && type != typeof(string)
            && type != typeof(decimal)
            && !type.IsPrimitive
            && !type.IsEnum
            && !IsFramework(type)
            && !HasCustomConverter(type);
    }

    // A type with its own JSON converter (a third-party struct written as a string, say) is a leaf: its
    // public properties are not the document's.
    private static bool HasCustomConverter(Type type) =>
        type.IsDefined(typeof(System.Text.Json.Serialization.JsonConverterAttribute), inherit: true)
        || KernelJson.Options.Converters.Any(c => c.CanConvert(type));

    /// <summary>
    /// Lists, under <paramref name="elementType"/>, every property that carries a policy attribute — what a
    /// collection of that element type would silently lose, because a collection is one atomic diff value.
    /// Descends into nested objects and nested collections; each entry reads <c>Type.Property [Attribute]</c>.
    /// </summary>
    /// <param name="elementType">The element type of a collection property.</param>
    public static IReadOnlyList<string> PolicyAttributesBeneath(Type elementType)
    {
        ArgumentNullException.ThrowIfNull(elementType);

        var found = new List<string>();
        Collect(elementType, found, visited: []);
        return found;
    }

    private static void Collect(Type type, List<string> found, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (ElementTypeOf(type) is { } element)
        {
            Collect(element, found, visited); // a list in a list: still no per-element path
            return;
        }

        if (!IsObjectShaped(type) || !visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (JsonPathResolver.IsIgnored(property))
            {
                continue;
            }

            if (PolicyAttributeReader.Of(property) is { } declared)
            {
                found.Add($"{type.Name}.{property.Name} [{declared.Attribute}]");
            }

            Collect(property.PropertyType, found, visited);
        }

        visited.Remove(type);
    }

    private static Type? GenericArgument(Type type, Type genericDefinition, int index)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition)
        {
            return type.GetGenericArguments()[index];
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == genericDefinition)
            {
                return contract.GetGenericArguments()[index];
            }
        }

        return null;
    }

    private static bool IsFramework(Type type)
    {
        var ns = type.Namespace;
        return ns is not null
            && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
                || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }
}
