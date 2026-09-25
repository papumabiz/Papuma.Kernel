// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

/// Keys declared from F#: F# lowers `fun x -> box x.Email` to a call to Operators.Box
/// rather than C#'s Convert node, and a tuple to a NewExpression — both must resolve to
/// the declared paths. Anonymous records are not supported (F# sorts their fields and
/// lowers construction to a block), so a composite key is declared with a tuple.
module Papuma.Kernel.FSharp.Tests.KeyTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Papuma.Kernel.FSharp.Tests.Infrastructure
open Papuma.Kernel.Model
open Papuma.Kernel.Store
open Xunit

type FsTicket = { Id: string; ProjectId: string; Number: int; Title: string }

let private keyPathOf (declare: KernelModelBuilder.DocumentTypeBuilder<FsTicket> -> unit) =
    let model = KernelModelBuilder().Document<FsTicket>(fun d -> declare d).Build()
    Assert.Single(model.GetRequired<FsTicket>().Keys).Path

[<Fact>]
let ``single-field key from an F# lambda`` () =
    Assert.Equal("number", keyPathOf (fun d -> d.UniqueKey(fun x -> box x.Number) |> ignore))

[<Fact>]
let ``composite key from an F# tuple`` () =
    Assert.Equal("projectId,number", keyPathOf (fun d -> d.UniqueKey(fun x -> box (x.ProjectId, x.Number)) |> ignore))

[<Fact>]
let ``an F# anonymous record is rejected with an ArgumentException`` () =
    Assert.Throws<ArgumentException>(fun () ->
        keyPathOf (fun d -> d.UniqueKey(fun x -> box {| ProjectId = x.ProjectId; Number = x.Number |}) |> ignore) |> ignore)
    |> ignore

[<Fact>]
let ``composite key from F# enforces uniqueness and loads by key`` () : Task =
    withModel (fun m -> m.Document<FsTicket>(fun d -> d.UniqueKey(fun x -> box (x.ProjectId, x.Number)) |> ignore) |> ignore) (fun store ->
        task {
            let session = store.OpenSession(newTenant ())
            try
                let id = newId ()
                let! _ = session.SaveAsync({ Id = id; ProjectId = "p1"; Number = 42; Title = "a" }, 0L)
                let! _ = Assert.ThrowsAsync<UniqueKeyViolationException>(fun () ->
                    session.SaveAsync({ Id = newId (); ProjectId = "p1"; Number = 42; Title = "b" }, 0L) :> Task)

                // F# picks the single-value overload for a bare array — annotate it.
                let values: IReadOnlyList<obj> = [| "p1" :> obj; 42 :> obj |]
                let! loaded = session.LoadByKeyAsync<FsTicket>((fun x -> box (x.ProjectId, x.Number)), values)
                match loaded with
                | null -> failwith "expected the ticket"
                | result -> Assert.Equal(id, result.Document.Id)
            finally
                session.DisposeAsync().AsTask().Wait()
        })
