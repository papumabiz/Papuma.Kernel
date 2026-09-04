// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.FSharp

open System.Linq.Expressions
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

/// <summary>
/// Converts a plain F# property-access quotation (<c>&lt;@ fun x -&gt; x.Field @&gt;</c>,
/// nested chains like <c>x.Address.City</c> included) directly into a LINQ
/// <c>Expression&lt;Func&lt;T,TValue&gt;&gt;</c> — hand-rolled instead of
/// <c>Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter</c>, because that
/// helper only bridges quotations that already construct a <c>System.Func</c>
/// (<c>&lt;@ Func&lt;_,_&gt;(fun x -&gt; …) @&gt;</c>); a plain quotation carries an F#
/// closure type it can't cast from. Confirmed empirically (dotnet fsi) against simple and
/// nested member access before landing here — same scope restriction as the C# side's
/// <c>JsonPathResolver.Resolve</c>: a simple property-access chain only, nothing richer.
/// </summary>
module internal QuotationExpr =

    let toExpression<'T, 'a> (quotation: Expr<'T -> 'a>) : Expression<System.Func<'T, 'a>> =
        match quotation with
        | Lambda(var, body) ->
            let param = Expression.Parameter(var.Type, var.Name)

            let rec build (e: Expr) : Expression =
                match e with
                | PropertyGet(Some inner, propInfo, []) -> Expression.Property(build inner, propInfo)
                | Var v when v = var -> param
                | Coerce(inner, _) -> build inner
                | _ ->
                    invalidArg
                        (nameof quotation)
                        $"Expected a simple property-access chain like <@ fun x -> x.Address.City @>, got: %A{e}"

            Expression.Lambda<System.Func<'T, 'a>>(build body, param)
        | _ ->
            invalidArg
                (nameof quotation)
                $"Expected a lambda quotation like <@ fun x -> x.Field @>, got: %A{quotation}"
