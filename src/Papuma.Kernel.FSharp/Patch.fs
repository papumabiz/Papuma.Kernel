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
/// <b>Deprecated.</b> The C# API works from F# directly: F# converts a lambda to a LINQ
/// expression at the method call, and the kernel unwraps F#'s <c>box</c> —
/// <c>p.Set((fun x -&gt; x.Name), v).Remove(fun x -&gt; box x.Note).Increment((fun x -&gt; box x.Count), n)</c>.
/// These members were built on the mistaken premise that F# lacks that conversion; the
/// real obstacle was the unrecognized <c>box</c> call, fixed in the kernel. They keep
/// working until their planned removal in 2.0.
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
        [<System.Obsolete("Use PatchBuilder.Set/Remove/Increment with an F# lambda: p.Set((fun x -> x.Name), v), p.Remove(fun x -> box x.Note), p.Increment((fun x -> box x.Count), n). Removal planned for 2.0.")>]
        member this.SetQ(property: Expr<'T -> 'a>, value: 'a) : PatchBuilder<'T> =
            this.Set(QuotationExpr.toExpression property, value)

        /// Quotation counterpart of <see cref="PatchBuilder{T}.Remove"/>.
        [<System.Obsolete("Use PatchBuilder.Set/Remove/Increment with an F# lambda: p.Set((fun x -> x.Name), v), p.Remove(fun x -> box x.Note), p.Increment((fun x -> box x.Count), n). Removal planned for 2.0.")>]
        member this.RemoveQ(property: Expr<'T -> 'a>) : PatchBuilder<'T> = this.Remove(toBoxedLambda property)

        /// Quotation counterpart of <see cref="PatchBuilder{T}.Increment"/>.
        [<System.Obsolete("Use PatchBuilder.Set/Remove/Increment with an F# lambda: p.Set((fun x -> x.Name), v), p.Remove(fun x -> box x.Note), p.Increment((fun x -> box x.Count), n). Removal planned for 2.0.")>]
        member this.IncrementQ(property: Expr<'T -> 'a>, ?by: int64) : PatchBuilder<'T> =
            this.Increment(toBoxedLambda property, defaultArg by 1L)
