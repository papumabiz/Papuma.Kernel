// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Linq.Expressions;

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Typed sugar over <see cref="ChangeRecord"/> — thin filters over the diff,
/// deliberately not an abstraction layer (architecture §8).
/// </summary>
public static class ChangeRecordExtensions
{
    /// <summary>
    /// Returns whether the given property changed in this record
    /// (e.g. <c>change.FieldChanged&lt;User&gt;(x =&gt; x.Email)</c>).
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="change">The change record.</param>
    /// <param name="property">The property access chain.</param>
    public static bool FieldChanged<T>(this ChangeRecord change, Expression<Func<T, object?>> property)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(property);
        return change.FieldChanged(JsonPathResolver.Resolve(property));
    }
}
