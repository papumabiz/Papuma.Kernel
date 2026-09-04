// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

/// End-to-end proof for Papuma.Kernel.FSharp.Session: `runSession` (the IAsyncDisposable
/// gap F#'s `use` doesn't cover) and the Result-returning trySaveAsync/tryPatchAsync
/// (KernelError instead of catching exceptions) against a real SQLite kernel.
module Papuma.Kernel.FSharp.Tests.SessionTests

open System
open System.Threading.Tasks
open Papuma.Kernel.FSharp
open Papuma.Kernel.FSharp.Tests.Infrastructure
open Papuma.Kernel.Model
open Papuma.Kernel.Store
open Xunit

type Account = { Id: string; Owner: string; Balance: int }

type UniqueAccount =
    { Id: string
      [<UniqueKey>]
      Owner: string
      Balance: int }

[<Fact>]
let ``runSession disposes the session even when the body throws`` () : Task =
    withStore<Account> (fun store ->
        task {
            let session = store.OpenSession(newTenant ())

            let! ex =
                Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                    runSession session (fun _ -> task { return raise (InvalidOperationException("boom")) }))

            Assert.Equal("boom", ex.Message)
        // No further assertion beyond "this returns instead of hanging" — a leaked SQLite
        // handle would surface as the fixture's own db-file cleanup failing (ClearPool
        // would find an open connection, File.Delete would throw).
        })

[<Fact>]
let ``trySaveAsync returns Ok on a normal insert and Error VersionConflict on a version race`` () : Task =
    withStore<Account> (fun store ->
        runSession
            (store.OpenSession(newTenant ()))
            (fun session ->
                task {
                    let id = newId ()

                    let! first = trySaveAsync session ({ Id = id; Owner = "Harry"; Balance = 100 }: Account) 0L
                    Assert.True(Result.isOk first)

                    // Second insert at the same expectedVersion 0 races against the first —
                    // the document already exists at version 1.
                    let! second = trySaveAsync session ({ Id = id; Owner = "Harry"; Balance = 200 }: Account) 0L

                    match second with
                    | Error(VersionConflict(documentType, documentId, expected, actual)) ->
                        Assert.Equal(nameof Account, documentType)
                        Assert.Equal(id, documentId)
                        Assert.Equal(0L, expected)
                        Assert.Equal(1L, actual)
                    | other -> Assert.Fail($"expected a VersionConflict, got: %A{other}")
                }))

[<Fact>]
let ``tryPatchAsync applies through SetQ and reports DocumentNotFound for a missing id`` () : Task =
    withStore<Account> (fun store ->
        runSession
            (store.OpenSession(newTenant ()))
            (fun session ->
                task {
                    let id = newId ()
                    let! _ = trySaveAsync session ({ Id = id; Owner = "Harry"; Balance = 100 }: Account) 0L

                    let! patched =
                        tryPatchAsync session id (fun p -> p.SetQ(<@ fun (x: Account) -> x.Owner @>, "Renamed") |> ignore) None

                    Assert.True(Result.isOk patched)

                    let! loaded = session.LoadAsync<Account>(id)

                    match loaded with
                    | null -> Assert.Fail("expected the document to still exist")
                    | loaded -> Assert.Equal("Renamed", loaded.Document.Owner)

                    let! missing =
                        tryPatchAsync
                            session
                            (newId ())
                            (fun p -> p.SetQ(<@ fun (x: Account) -> x.Owner @>, "Nobody") |> ignore)
                            None

                    match missing with
                    | Error(DocumentNotFound(documentType, _)) -> Assert.Equal(nameof Account, documentType)
                    | other -> Assert.Fail($"expected a DocumentNotFound, got: %A{other}")
                }))

[<Fact>]
let ``trySaveAsync reports UniqueKeyViolation`` () : Task =
    withStore<UniqueAccount> (fun store ->
        runSession
            (store.OpenSession(newTenant ()))
            (fun session ->
                task {
                    let! _ = trySaveAsync session ({ Id = newId (); Owner = "Harry"; Balance = 100 }: UniqueAccount) 0L
                    let! second = trySaveAsync session ({ Id = newId (); Owner = "Harry"; Balance = 200 }: UniqueAccount) 0L

                    match second with
                    | Error(UniqueKeyViolation(documentType, keyPath)) ->
                        Assert.Equal(nameof UniqueAccount, documentType)
                        Assert.Equal("owner", keyPath)
                    | other -> Assert.Fail($"expected a UniqueKeyViolation, got: %A{other}")
                }))
