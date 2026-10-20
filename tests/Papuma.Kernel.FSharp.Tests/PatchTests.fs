// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

/// End-to-end proof that Papuma.Kernel.FSharp's quotation-based Patch extensions
/// (SetQ/RemoveQ/IncrementQ) actually round-trip through a real SQLite kernel — the C#
/// counterparts are tested this way in
/// Papuma.Kernel.Local.Tests/Store/SqlitePatchGdprHistoryRollbackTests.cs; this mirrors
/// those two cases.
module Papuma.Kernel.FSharp.Tests.PatchTests

// SetQ/RemoveQ/IncrementQ are deprecated but supported until 2.0 — keep testing them.
#nowarn "44"

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

/// Plain F# lambdas work against the C# Patch API as well: F# converts a lambda to a LINQ
/// expression at the method call; `box` (needed for the object-typed Remove/Increment
/// parameters) lowers to a call to Operators.Box, which the kernel's path resolver unwraps.
[<Fact>]
let ``plain F# lambdas apply through the C# Patch API`` () : Task =
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
                            p.Set((fun x -> x.Name), "renamed")
                             .Remove(fun x -> box x.Note)
                             .Increment((fun x -> box x.Value), 3L)
                            |> ignore
                    )

                let! loaded = session.LoadAsync<Counter>(id)

                match loaded with
                | null -> Assert.Fail("expected the document to still exist")
                | loaded ->
                    Assert.Equal("renamed", loaded.Document.Name)
                    Assert.Null(loaded.Document.Note)
                    Assert.Equal(3, loaded.Document.Value)
            finally
                session.DisposeAsync().AsTask() |> Async.AwaitTask |> Async.RunSynchronously
        })

// ── Negative cases for the quotation converter (review F-3) ─────────────────────

type WithOption =
    { Id: string
      Count: int
      Label: string option
      Tags: string list }

/// Runs a patch with a quotation the converter must not accept and returns what it threw.
let private patchWith (patch: PatchBuilder<WithOption> -> unit) : Task<exn option> =
    task {
        let mutable thrown: exn option = None

        do!
            withStore<WithOption> (fun store ->
                task {
                    let session = store.OpenSession(newTenant ())

                    try
                        let id = newId ()
                        let! _ = session.SaveAsync({ Id = id; Count = 1; Label = None; Tags = [] }, 0L)

                        try
                            let! _ = session.PatchAsync<WithOption>(id, fun p -> patch p)
                            ()
                        with ex ->
                            thrown <- Some ex
                    finally
                        session.DisposeAsync().AsTask() |> Async.AwaitTask |> Async.RunSynchronously
                })

        return thrown
    }

[<Fact>]
let ``a method call in the quotation is rejected with an ArgumentException naming the shape`` () : Task =
    task {
        let! thrown = patchWith (fun p -> p.SetQ(<@ fun (x: WithOption) -> x.Id.ToUpper() @>, "x") |> ignore)
        let ex = Option.get thrown
        let arg = Assert.IsAssignableFrom<System.ArgumentException>(ex)
        Assert.Contains("simple property-access chain", arg.Message)
    }

[<Fact>]
let ``a constant instead of a property access is rejected`` () : Task =
    task {
        let! thrown = patchWith (fun p -> p.SetQ(<@ fun (_: WithOption) -> "constant" @>, "x") |> ignore)
        let ex = Option.get thrown
        let arg = Assert.IsAssignableFrom<System.ArgumentException>(ex)
        Assert.Contains("simple property-access chain", arg.Message)
    }

[<Fact>]
let ``an indexer or list access is rejected`` () : Task =
    task {
        let! thrown = patchWith (fun p -> p.SetQ(<@ fun (x: WithOption) -> x.Tags.[0] @>, "x") |> ignore)
        Assert.IsAssignableFrom<System.ArgumentException>(Option.get thrown) |> ignore
    }

[<Fact>]
let ``F# box on a property is rejected as not being a plain property access`` () : Task =
    task {
        // `box` is a call in the quotation, not a type coercion, so the converter refuses it with
        // the same explanation as any other call — it never guesses a path.
        let! thrown = patchWith (fun p -> p.RemoveQ(<@ fun (x: WithOption) -> box x.Count @>) |> ignore)

        let arg = Assert.IsAssignableFrom<System.ArgumentException>(Option.get thrown)
        Assert.Contains("simple property-access chain", arg.Message)
    }

[<Fact>]
let ``an option chain through Value is not caught by the converter but fails closed at patch time`` () : Task =
    task {
        // x.Label.Value converts to the path 'label.value'; option fields are not supported in
        // documents (README), so no stored document has that path and the patch is refused —
        // it never writes anywhere else.
        let! thrown = patchWith (fun p -> p.SetQ(<@ fun (x: WithOption) -> x.Label.Value @>, "x") |> ignore)

        let ex = Assert.IsType<PatchTargetMissingException>(Option.get thrown)
        Assert.Contains("label.value", ex.Message)
    }
