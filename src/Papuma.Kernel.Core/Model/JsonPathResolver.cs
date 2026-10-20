// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

using Papuma.Kernel.Changes;

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

            if (IsNullableValue(property))
            {
                // x => x.Address.Value.Street on a Nullable<struct>: "Value" is the unwrapping the C# compiler
                // asks for, not a JSON property — the serializer writes the struct's own members at this level
                // (review N-9). Taking it as a segment gave "address.value.street": a policy that matches nothing.
                current = Unwrap(member.Expression!);
                continue;
            }

            if (IsIgnored(property))
            {
                throw new ArgumentException(
                    $"{property.DeclaringType?.Name}.{property.Name} is never serialized ([JsonIgnore]), so it has no " +
                    "JSON path: a policy or key declared on it would silently do nothing. Remove the declaration, or " +
                    "remove [JsonIgnore] if the property belongs in the document (review C-19).",
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

        return DiffPath.Join(segments);
    }

    /// <summary>
    /// Returns whether a property is <b>never</b> serialized: <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>
    /// without a condition, or with <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.Always"/>. Such a
    /// property has no JSON path, so declaring a policy or key on it would be a no-op — the metamodel scan skips it so
    /// the inventory lists no phantom paths (review L7).
    /// </summary>
    /// <remarks>
    /// A <i>conditional</i> ignore (<c>WhenWritingNull</c>, <c>WhenWritingDefault</c>) is not "never": the property
    /// appears in the document whenever it has a value, so its policy must apply. Treating every
    /// <c>[JsonIgnore]</c> alike silently switched off <c>[SensitiveData]</c> and <c>[TrackHash]</c> for a property
    /// that also said <c>WhenWritingNull</c> — its value went into the feed in clear text.
    /// </remarks>
    public static bool IsIgnored(PropertyInfo property) =>
        property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is { } ignore
        && ignore.Condition == System.Text.Json.Serialization.JsonIgnoreCondition.Always;

    /// <summary>
    /// Returns the serialized JSON name of a property (naming policy applied,
    /// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> respected).
    /// </summary>
    public static string JsonNameOf(PropertyInfo property)
    {
        var explicitName = property
            .GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name;
        return explicitName
            ?? KernelJson.Options.PropertyNamingPolicy?.ConvertName(property.Name)
            ?? property.Name;
    }

    private static bool IsNullableValue(PropertyInfo property) =>
        property.Name == "Value"
        && property.DeclaringType is { IsGenericType: true } declaring
        && declaring.GetGenericTypeDefinition() == typeof(Nullable<>);

    // Strips the boxing a lambda to object introduces: C# emits Convert, F# emits a call to
    // Operators.Box (`fun x -> box x.Email`). Matched by name so Core needs no FSharp.Core.
    private static Expression Unwrap(Expression expression) => expression switch
    {
        UnaryExpression { NodeType: ExpressionType.Convert } unary => unary.Operand,
        MethodCallExpression { Method.Name: "Box", Arguments.Count: 1 } call
            when call.Method.DeclaringType?.FullName == "Microsoft.FSharp.Core.Operators" => call.Arguments[0],
        _ => expression,
    };
}
