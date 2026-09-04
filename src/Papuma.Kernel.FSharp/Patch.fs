// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.FSharp

open System.Linq.Expressions
open Microsoft.FSharp.Quotations
open Papuma.Kernel.Store

/// <summary>
/// F#-idiomatic counterpart to <see cref="PatchBuilder{T}"/>'s <c>Set</c>/<c>Remove</c>/
/// <c>Increment</c> (ADR-012), via <see cref="QuotationExpr"/>:
/// <code>
/// session.PatchAsync&lt;Counter&gt;(id, fun p ->
///     p.SetQ(&lt;@ fun x -> x.Name @&gt;, "renamed")
///      .IncrementQ(&lt;@ fun x -> x.Value @&gt;, 3L)
///     |&gt; ignore)
/// </code>
///
/// Prototype, not a decided product surface (2026-09-04 chat) — companion to
/// docs/analyses/offline-sync-and-projection-conflicts.md in spirit: additive, no kernel
/// change. Same scope restriction as the C# API it mirrors: a quotation must be a simple
/// property-access chain (<c>x.Field</c>, <c>x.Nested.Field</c>) — anything richer fails
/// the same way <c>JsonPathResolver.Resolve</c> already rejects it on the C# side.
/// </summary>
[<AutoOpen>]
module PatchExtensions =

    /// `Remove`/`Increment` need `Expression<Func<T, object?>>` regardless of the field's
    /// actual CLR type — box the resolved body (mirrors what the C# compiler emits
    /// automatically for those parameters).
    let private toBoxedLambda (quotation: Expr<'T -> 'a>) : Expression<System.Func<'T, obj>> =
        let lambda = QuotationExpr.toExpression quotation
        let boxedBody = Expression.Convert(lambda.Body, typeof<obj>) :> Expression
        Expression.Lambda<System.Func<'T, obj>>(boxedBody, lambda.Parameters)

    type PatchBuilder<'T when 'T: not struct and 'T: not null> with

        /// Quotation counterpart of <see cref="PatchBuilder{T}.Set"/>.
        member this.SetQ(property: Expr<'T -> 'a>, value: 'a) : PatchBuilder<'T> =
            this.Set(QuotationExpr.toExpression property, value)

        /// Quotation counterpart of <see cref="PatchBuilder{T}.Remove"/>.
        member this.RemoveQ(property: Expr<'T -> 'a>) : PatchBuilder<'T> = this.Remove(toBoxedLambda property)

        /// Quotation counterpart of <see cref="PatchBuilder{T}.Increment"/>.
        member this.IncrementQ(property: Expr<'T -> 'a>, ?by: int64) : PatchBuilder<'T> =
            this.Increment(toBoxedLambda property, defaultArg by 1L)
