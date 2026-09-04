// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

/// End-to-end proof that Papuma.Kernel.FSharp's quotation-based Patch extensions
/// (SetQ/RemoveQ/IncrementQ) actually round-trip through a real SQLite kernel — the C#
/// counterparts are tested this way in
/// Papuma.Kernel.Local.Tests/Store/SqlitePatchGdprHistoryRollbackTests.cs; this mirrors
/// those two cases.
module Papuma.Kernel.FSharp.Tests.PatchTests

open System.Threading.Tasks
open Papuma.Kernel.FSharp
open Papuma.Kernel.FSharp.Tests.Infrastructure
open Papuma.Kernel.Store
open Xunit

type Counter =
    { Id: string
      Name: string
      Value: int
      Note: string }

[<Fact>]
let ``SetQ and RemoveQ apply through the quotation-based Patch API`` () : Task =
    withStore<Counter> (fun store ->
        task {
            let session = store.OpenSession(newTenant ())

            try
                let id = newId ()
                let! _ = session.SaveAsync({ Id = id; Name = "hits"; Value = 0; Note = "old" }, 0L)

                let! _ =
                    session.PatchAsync<Counter>(
                        id,
                        fun p ->
                            p.SetQ(<@ fun (x: Counter) -> x.Name @>, "renamed")
                             .RemoveQ(<@ fun (x: Counter) -> x.Note @>)
                            |> ignore
                    )

                let! loaded = session.LoadAsync<Counter>(id)

                match loaded with
                | null -> Assert.Fail("expected the document to still exist")
                | loaded ->
                    Assert.Equal("renamed", loaded.Document.Name)
                    Assert.Null(loaded.Document.Note)
            finally
                session.DisposeAsync().AsTask() |> Async.AwaitTask |> Async.RunSynchronously
        })

[<Fact>]
let ``IncrementQ is atomic and cumulative`` () : Task =
    withStore<Counter> (fun store ->
        task {
            let session = store.OpenSession(newTenant ())

            try
                let id = newId ()
                let! _ = session.SaveAsync({ Id = id; Name = "hits"; Value = 0; Note = "" }, 0L)

                let! _ =
                    session.PatchAsync<Counter>(
                        id,
                        fun p -> p.IncrementQ(<@ fun (x: Counter) -> x.Value @>, 3L) |> ignore
                    )

                let! result =
                    session.PatchAsync<Counter>(
                        id,
                        fun p -> p.IncrementQ(<@ fun (x: Counter) -> x.Value @>, 2L) |> ignore
                    )

                Assert.Equal(3L, result.Version)
                let! loaded = session.LoadAsync<Counter>(id)

                match loaded with
                | null -> Assert.Fail("expected the document to still exist")
                | loaded -> Assert.Equal(5, loaded.Document.Value)
            finally
                session.DisposeAsync().AsTask() |> Async.AwaitTask |> Async.RunSynchronously
        })
