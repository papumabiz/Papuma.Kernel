// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.FSharp

open System
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Papuma.Kernel.Store

/// <summary>
/// F#-idiomatic wrappers around a document session's write path. Written against
/// statically resolved type parameters (SRTP), not a shared base type — <c>DocumentSession</c>
/// (Postgres) and <c>SqliteDocumentSession</c> (SQLite) are two unrelated sealed classes
/// with identical method shapes ("mirrors the shape", see docs/analyses/local-kernel-sqlite-sibling.md),
/// so this is the one place that duck-typing genuinely earns its keep: one implementation
/// covers both kernels without picking a dependency on either concrete package. Everything
/// here is additive — it calls the existing <c>SaveAsync</c>/<c>PatchAsync</c>, catches the
/// three expected exceptions (<see cref="KernelError"/>) and rethrows anything else
/// unchanged; nothing about the C# surface changes.
/// <para>
/// Cancellation: F# module functions cannot take an optional parameter, so every function
/// that talks to the database has a <c>…WithToken…</c> twin taking the
/// <see cref="CancellationToken"/> as its first argument; the plain name passes
/// <see cref="CancellationToken.None"/>.
/// </para>
/// </summary>
[<AutoOpen>]
module Session =

    /// <summary>
    /// Runs <paramref name="f"/> against an already-open session and disposes it
    /// afterwards, including on failure. F#'s <c>use</c> doesn't bind
    /// <see cref="IAsyncDisposable"/> (only <see cref="IDisposable"/>) — this fills that
    /// gap so callers don't have to hand-write the try/finally + <c>DisposeAsync</c> dance.
    /// </summary>
    /// <remarks>
    /// The dispose is awaited, never blocked on: closing the connection and rolling back an
    /// uncommitted transaction are real I/O, and a blocking wait would hold a thread (and
    /// deadlock under a single-threaded <see cref="SynchronizationContext"/>, e.g. a UI
    /// thread). F#'s <c>task</c> builder does not support <c>do!</c> inside a
    /// <c>finally</c> (FS0750), so the outcome of <paramref name="f"/> is buffered, the
    /// session is disposed, and the outcome is replayed afterwards — an exception keeps its
    /// original stack trace (<see cref="ExceptionDispatchInfo"/>).
    /// <para>
    /// If both <paramref name="f"/> and the dispose throw, the exception of
    /// <paramref name="f"/> is the one the caller sees — the dispose failure must not hide
    /// the cause. It is attached to that exception as
    /// <c>Data["Papuma.DisposeException"]</c> (its <c>ToString()</c>). If only the dispose
    /// throws, it propagates.
    /// </para>
    /// </remarks>
    let runSession<'Session, 'a when 'Session :> IAsyncDisposable> (session: 'Session) (f: 'Session -> Task<'a>) : Task<'a> =
        task {
            let mutable result = Unchecked.defaultof<'a>
            let mutable failure: ExceptionDispatchInfo option = None

            try
                let! value = f session
                result <- value
            with ex ->
                failure <- Some(ExceptionDispatchInfo.Capture ex)

            try
                do! session.DisposeAsync().AsTask()
            with disposeEx ->
                match failure with
                | None -> failure <- Some(ExceptionDispatchInfo.Capture disposeEx)
                | Some original -> original.SourceException.Data["Papuma.DisposeException"] <- disposeEx.ToString()

            match failure with
            | Some edi -> edi.Throw()
            | None -> ()

            return result
        }

    /// <summary>
    /// Like <see cref="runSession"/>, for a body that returns a <c>Result</c>: commits the
    /// session when it returns <c>Ok</c>, discards its writes when it returns <c>Error</c>,
    /// and disposes it either way. <c>Ok</c> then really means "done" — the forgotten
    /// <c>CommitAsync</c> after a successful <c>trySaveAsync</c> (feedback F-18) cannot
    /// happen. An exception still disposes without commit, which rolls back and logs.
    /// <paramref name="ct"/> goes to the commit and the discard; a cancelled token before the
    /// commit leaves the writes uncommitted (the dispose rolls them back).
    /// </summary>
    let inline runSessionCommittedWithToken< ^Session, 'a, 'e
        when ^Session :> IAsyncDisposable
        and ^Session: (member CommitAsync: CancellationToken -> Task)
        and ^Session: (member DiscardAsync: CancellationToken -> Task)>
        (ct: CancellationToken)
        (session: ^Session)
        (f: ^Session -> Task<Result<'a, 'e>>)
        : Task<Result<'a, 'e>> =
        let commit () = (^Session: (member CommitAsync: CancellationToken -> Task) (session, ct))
        let discard () = (^Session: (member DiscardAsync: CancellationToken -> Task) (session, ct))
        runSession session (fun s ->
            task {
                let! result = f s
                match result with
                | Ok _ -> do! commit ()
                | Error _ -> do! discard ()
                return result
            })

    /// <summary>
    /// <see cref="runSessionCommittedWithToken"/> without a cancellation token
    /// (<see cref="CancellationToken.None"/>).
    /// </summary>
    let inline runSessionCommitted< ^Session, 'a, 'e
        when ^Session :> IAsyncDisposable
        and ^Session: (member CommitAsync: CancellationToken -> Task)
        and ^Session: (member DiscardAsync: CancellationToken -> Task)>
        (session: ^Session)
        (f: ^Session -> Task<Result<'a, 'e>>)
        : Task<Result<'a, 'e>> =
        runSessionCommittedWithToken CancellationToken.None session f

    /// <summary>
    /// <see cref="KernelError"/>-returning counterpart of <c>SaveAsync</c>, passing
    /// <paramref name="ct"/> on. Cancellation is not a <see cref="KernelError"/>: it surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    let inline trySaveWithTokenAsync< ^Session, 'T
        when ^Session: (member SaveAsync: 'T * int64 * CancellationToken -> Task<SaveResult>)>
        (ct: CancellationToken)
        (session: ^Session)
        (document: 'T)
        (expectedVersion: int64)
        : Task<Result<SaveResult, KernelError>> =
        task {
            try
                let! result = (^Session: (member SaveAsync: 'T * int64 * CancellationToken -> Task<SaveResult>) (session, document, expectedVersion, ct))
                return Ok result
            with
            | KernelErrorClassifier.Known err -> return Error err
        }

    /// <summary>
    /// <see cref="KernelError"/>-returning counterpart of <c>SaveAsync</c>
    /// (<see cref="CancellationToken.None"/>; see <see cref="trySaveWithTokenAsync"/>).
    /// </summary>
    let inline trySaveAsync< ^Session, 'T
        when ^Session: (member SaveAsync: 'T * int64 * CancellationToken -> Task<SaveResult>)>
        (session: ^Session)
        (document: 'T)
        (expectedVersion: int64)
        : Task<Result<SaveResult, KernelError>> =
        trySaveWithTokenAsync CancellationToken.None session document expectedVersion

    /// <summary>
    /// <see cref="KernelError"/>-returning counterpart of <c>PatchAsync</c>, passing
    /// <paramref name="ct"/> on. <paramref name="expectedVersion"/> follows ADR-012: <c>None</c>
    /// means deliberate field-level last-writer-wins, not "don't care about conflicts entirely".
    /// Cancellation surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    let inline tryPatchWithTokenAsync< ^Session, 'T
        when ^Session: (member PatchAsync: string * Action<PatchBuilder<'T>> * Nullable<int64> * CancellationToken -> Task<SaveResult>)>
        (ct: CancellationToken)
        (session: ^Session)
        (id: string)
        (patch: PatchBuilder<'T> -> unit)
        (expectedVersion: int64 option)
        : Task<Result<SaveResult, KernelError>> =
        task {
            try
                let! result =
                    (^Session: (member PatchAsync: string * Action<PatchBuilder<'T>> * Nullable<int64> * CancellationToken -> Task<SaveResult>)
                        (session, id, Action<_>(patch), Option.toNullable expectedVersion, ct))
                return Ok result
            with
            | KernelErrorClassifier.Known err -> return Error err
        }

    /// <summary>
    /// <see cref="KernelError"/>-returning counterpart of <c>PatchAsync</c>
    /// (<see cref="CancellationToken.None"/>; see <see cref="tryPatchWithTokenAsync"/>).
    /// </summary>
    let inline tryPatchAsync< ^Session, 'T
        when ^Session: (member PatchAsync: string * Action<PatchBuilder<'T>> * Nullable<int64> * CancellationToken -> Task<SaveResult>)>
        (session: ^Session)
        (id: string)
        (patch: PatchBuilder<'T> -> unit)
        (expectedVersion: int64 option)
        : Task<Result<SaveResult, KernelError>> =
        tryPatchWithTokenAsync CancellationToken.None session id patch expectedVersion
