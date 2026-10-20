// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

/// End-to-end proof for Papuma.Kernel.FSharp.Session: `runSession` (the IAsyncDisposable
/// gap F#'s `use` doesn't cover) and the Result-returning trySaveAsync/tryPatchAsync
/// (KernelError instead of catching exceptions) against a real SQLite kernel.
module Papuma.Kernel.FSharp.Tests.SessionTests

open System
open System.Threading
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
let ``tryPatchAsync applies a patch and reports DocumentNotFound for a missing id`` () : Task =
    withStore<Account> (fun store ->
        runSession
            (store.OpenSession(newTenant ()))
            (fun session ->
                task {
                    let id = newId ()
                    let! _ = trySaveAsync session ({ Id = id; Owner = "Harry"; Balance = 100 }: Account) 0L

                    let! patched =
                        tryPatchAsync session id (fun p -> p.Set((fun (x: Account) -> x.Owner), "Renamed") |> ignore) None

                    Assert.True(Result.isOk patched)

                    let! loaded = session.LoadAsync<Account>(id)

                    match loaded with
                    | null -> Assert.Fail("expected the document to still exist")
                    | loaded -> Assert.Equal("Renamed", loaded.Document.Owner)

                    let! missing =
                        tryPatchAsync
                            session
                            (newId ())
                            (fun p -> p.Set((fun (x: Account) -> x.Owner), "Nobody") |> ignore)
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

[<Fact>]
let ``runSessionCommitted commits on Ok and discards on Error`` () : Task =
    withStore<Account> (fun store ->
        task {
            let tenant = newTenant ()
            let kept = newId ()
            let dropped = newId ()

            let! ok =
                runSessionCommitted (store.OpenSession(tenant)) (fun session ->
                    trySaveAsync session ({ Id = kept; Owner = "Harry"; Balance = 100 }: Account) 0L)

            Assert.True(Result.isOk ok)

            let! error =
                runSessionCommitted (store.OpenSession(tenant)) (fun session ->
                    task {
                        let! _ = trySaveAsync session ({ Id = dropped; Owner = "Sally"; Balance = 1 }: Account) 0L
                        return Error "rejected by the command"
                    })

            Assert.Equal(Error "rejected by the command", error)

            do!
                runSession (store.OpenSession(tenant)) (fun session ->
                    task {
                        let! committed = session.LoadAsync<Account>(kept)
                        let! discarded = session.LoadAsync<Account>(dropped)
                        Assert.NotNull(committed)
                        Assert.Null(discarded)
                    })
        })

// ── Dispose without blocking, original exception wins (review F-2) ─────────────

/// An <see cref="IAsyncDisposable"/> whose dispose does whatever the test says.
type private FakeSession(onDispose: unit -> Task) =
    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(onDispose ())

[<Fact>]
let ``runSession surfaces the body's exception when the dispose throws as well`` () : Task =
    task {
        let session =
            FakeSession(fun () ->
                task {
                    do! Task.Yield()
                    raise (InvalidOperationException("dispose failed"))
                })

        let! ex =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                runSession session (fun _ -> task { return raise (InvalidOperationException("body failed")) }))

        Assert.Equal("body failed", ex.Message)
        // The dispose failure is not lost — it hangs on the exception the caller sees.
        Assert.Contains("dispose failed", string ex.Data["Papuma.DisposeException"])
    }

[<Fact>]
let ``runSession surfaces a dispose failure when the body succeeded`` () : Task =
    task {
        let session = FakeSession(fun () -> task { raise (InvalidOperationException("dispose failed")) })

        let! ex =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                runSession session (fun _ -> task { return 42 }))

        Assert.Equal("dispose failed", ex.Message)
    }

[<Fact>]
let ``runSession returns the body's result and disposes exactly once`` () : Task =
    task {
        let mutable disposed = 0

        let session =
            FakeSession(fun () ->
                task {
                    do! Task.Yield()
                    disposed <- disposed + 1
                })

        let! result = runSession session (fun _ -> task { return 42 })

        Assert.Equal(42, result)
        Assert.Equal(1, disposed)
    }

/// Runs <paramref name="f"/> on a dedicated thread whose SynchronizationContext is a
/// single-threaded message pump (like a UI thread): continuations posted to it run only
/// when the pump thread is free.
let private runOnPumpThread (f: unit -> Task<'a>) : Task<'a> =
    let queue = new System.Collections.Concurrent.BlockingCollection<struct (SendOrPostCallback * (obj | null))>()

    let context =
        { new SynchronizationContext() with
            member _.Post(callback, state) = queue.Add(struct (callback, state)) }

    let completion = TaskCompletionSource<'a>(TaskCreationOptions.RunContinuationsAsynchronously)

    let pump () =
        SynchronizationContext.SetSynchronizationContext context

        queue.Add(
            struct (SendOrPostCallback(fun _ ->
                        (f ()).ContinueWith(fun (t: Task<'a>) ->
                            if t.IsFaulted then completion.SetException((nonNull t.Exception).InnerExceptions)
                            elif t.IsCanceled then completion.SetCanceled()
                            else completion.SetResult(t.Result))
                        |> ignore),
                    null)
        )

        for struct (callback, state) in queue.GetConsumingEnumerable() do
            callback.Invoke state

    let thread = Thread(pump, IsBackground = true)
    thread.Start()
    completion.Task

[<Fact>]
let ``runSession does not block a single-threaded SynchronizationContext while disposing`` () : Task =
    task {
        // The dispose needs the pump to continue after its Yield. A blocking wait inside
        // runSession would hold the pump thread and never finish.
        let session = FakeSession(fun () -> task { do! Task.Yield() })

        let work = runOnPumpThread (fun () -> runSession session (fun _ -> task { return 7 }))
        let! finished = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds 10.0))

        Assert.True(obj.ReferenceEquals(finished, work), "runSession deadlocked on the single-threaded context")
        Assert.Equal(7, work.Result)
    }

// ── Cancellation (review F-1) ──────────────────────────────────────────────────

[<Fact>]
let ``trySaveWithTokenAsync and tryPatchWithTokenAsync throw OperationCanceledException for a cancelled token`` () : Task =
    withStore<Account> (fun store ->
        runSession
            (store.OpenSession(newTenant ()))
            (fun session ->
                task {
                    use cts = new Threading.CancellationTokenSource()
                    cts.Cancel()

                    let! _ =
                        Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                            trySaveWithTokenAsync cts.Token session ({ Id = newId (); Owner = "Harry"; Balance = 1 }: Account) 0L)

                    let! _ =
                        Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                            tryPatchWithTokenAsync
                                cts.Token
                                session
                                (newId ())
                                (fun p -> p.Set((fun (x: Account) -> x.Owner), "X") |> ignore)
                                None)

                    ()
                }))

[<Fact>]
let ``runSessionCommittedWithToken does not commit when the token is cancelled before the commit`` () : Task =
    withStore<Account> (fun store ->
        task {
            let tenant = newTenant ()
            let id = newId ()
            use cts = new Threading.CancellationTokenSource()

            let! _ =
                Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                    runSessionCommittedWithToken cts.Token (store.OpenSession(tenant)) (fun session ->
                        task {
                            let! saved = trySaveAsync session ({ Id = id; Owner = "Harry"; Balance = 1 }: Account) 0L
                            cts.Cancel() // the caller gave up while the body was running
                            return saved
                        }))

            do!
                runSession (store.OpenSession(tenant)) (fun session ->
                    task {
                        let! loaded = session.LoadAsync<Account>(id)
                        Assert.Null(loaded)
                    })
        })

[<Fact>]
let ``the token-less functions still work and the token variants pass a live token through`` () : Task =
    withStore<Account> (fun store ->
        task {
            let tenant = newTenant ()
            let id = newId ()
            use cts = new Threading.CancellationTokenSource()

            let! result =
                runSessionCommittedWithToken cts.Token (store.OpenSession(tenant)) (fun session ->
                    trySaveWithTokenAsync cts.Token session ({ Id = id; Owner = "Harry"; Balance = 1 }: Account) 0L)

            Assert.True(Result.isOk result)

            do!
                runSession (store.OpenSession(tenant)) (fun session ->
                    task {
                        let! loaded = session.LoadAsync<Account>(id)
                        Assert.NotNull(loaded)
                    })
        })
