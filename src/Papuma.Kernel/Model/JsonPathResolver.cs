// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.Model;

/// <summary>
/// Resolves property access expressions and reflection members to the JSON paths
/// used by the diff engine and key indexes (serializer naming policy applied).
/// </summary>
internal static class JsonPathResolver
{
    /// <summary>
    /// Resolves a property access chain (e.g. <c>x => x.Address.City</c>) to a
    /// dot-separated JSON path (<c>address.city</c>).
    /// </summary>
    public static string Resolve(LambdaExpression propertyExpression)
    {
        ArgumentNullException.ThrowIfNull(propertyExpression);

        var segments = new Stack<string>();
        var current = Unwrap(propertyExpression.Body);

        while (current is MemberExpression member)
        {
            if (member.Member is not PropertyInfo property)
            {
                throw new ArgumentException(
                    $"Expression must be a property access chain; '{member.Member.Name}' is not a property.",
                    nameof(propertyExpression));
            }

            segments.Push(JsonNameOf(property));
            current = Unwrap(member.Expression!);
        }

        if (current is not ParameterExpression || segments.Count == 0)
        {
            throw new ArgumentException(
                "Expression must be a simple property access chain like x => x.Address.City.",
                nameof(propertyExpression));
        }

        return string.Join('.', segments);
    }

    /// <summary>
    /// Returns whether a property is excluded from serialization via
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>. Such properties
    /// have no JSON path, so declaring a policy or key on them would be a no-op — the
    /// metamodel scan skips them so the inventory lists no phantom paths (review L7).
    /// </summary>
    public static bool IsIgnored(PropertyInfo property) =>
        property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null;

    /// <summary>
    /// Returns the serialized JSON name of a property (naming policy applied,
    /// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> respected).
    /// </summary>
    public static string JsonNameOf(PropertyInfo property)
    {
        var explicitName = property
            .GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name;
        if (explicitName is not null)
        {
            return explicitName;
        }

        return KernelJson.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
    }

    private static Expression Unwrap(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert } unary ? unary.Operand : expression;
}
