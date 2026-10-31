// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
        return change.FieldChanged(JsonPathResolver.Resolve(property, nameof(property)));
    }

    /// <summary>
    /// Returns whether this record is a change of documents of type <typeparamref name="T"/>
    /// (e.g. <c>if (!change.Is&lt;Order&gt;(model)) return;</c>). Compares the record's logical name with the one the
    /// model gives <typeparamref name="T"/> — from <c>[DocumentType]</c>, <c>.Named(…)</c> or the class name
    /// (ADR-028). Comparing <see cref="ChangeRecord.DocumentType"/> with <c>nameof(Order)</c> instead stops matching,
    /// without an error, the day the type gets a logical name of its own. Handlers take the
    /// <see cref="KernelModel"/> from dependency injection.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="change">The change record.</param>
    /// <param name="model">The kernel model the record was written with.</param>
    /// <exception cref="DocumentTypeNotRegisteredException"><typeparamref name="T"/> is not registered in <paramref name="model"/>.</exception>
    public static bool Is<T>(this ChangeRecord change, KernelModel model)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(model);
        return string.Equals(change.DocumentType, model.GetRequired<T>().Name, StringComparison.Ordinal);
    }
}
