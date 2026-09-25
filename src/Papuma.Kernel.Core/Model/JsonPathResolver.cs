// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

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
        return ResolveChain(propertyExpression.Body, nameof(propertyExpression));
    }

    /// <summary>
    /// Resolves a key declaration: a single property chain (<c>x => x.Email</c>) or an
    /// anonymous type of property chains (<c>x => new { x.ProjectId, x.Number }</c>) for a
    /// composite key (ADR-020). Returns the paths comma-separated in declaration order —
    /// the form of <see cref="KeyMetadata.Path"/>.
    /// </summary>
    public static string ResolveKey(LambdaExpression keyExpression)
    {
        ArgumentNullException.ThrowIfNull(keyExpression);

        if (Unwrap(keyExpression.Body) is not NewExpression composite)
        {
            return EnsureNoSeparator(ResolveChain(keyExpression.Body, nameof(keyExpression)));
        }

        if (composite.Arguments.Count == 0)
        {
            throw new ArgumentException("A composite key needs at least one property.", nameof(keyExpression));
        }

        var paths = composite.Arguments
            .Select(a => EnsureNoSeparator(ResolveChain(a, nameof(keyExpression))))
            .ToList();
        var duplicate = paths.GroupBy(p => p, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Composite key lists '{duplicate.Key}' more than once.", nameof(keyExpression));
        }

        return string.Join(KeyMetadata.ComponentSeparator, paths);
    }

    private static string EnsureNoSeparator(string path) =>
        path.Contains(KeyMetadata.ComponentSeparator)
            ? throw new ArgumentException(
                $"Key path '{path}' contains '{KeyMetadata.ComponentSeparator}', which separates composite key components.",
                "keyExpression")
            : path;

    private static string ResolveChain(Expression body, string parameterName)
    {
        var segments = new Stack<string>();
        var current = Unwrap(body);

        while (current is MemberExpression member)
        {
            if (member.Member is not PropertyInfo property)
            {
                throw new ArgumentException(
                    $"Expression must be a property access chain; '{member.Member.Name}' is not a property.",
                    parameterName);
            }

            segments.Push(JsonNameOf(property));
            current = Unwrap(member.Expression!);
        }

        if (current is not ParameterExpression || segments.Count == 0)
        {
            throw new ArgumentException(
                "Expression must be a simple property access chain like x => x.Address.City.",
                parameterName);
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
