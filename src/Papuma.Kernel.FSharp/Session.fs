// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.FSharp

open System
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
/// </summary>
[<AutoOpen>]
module Session =

    /// <summary>
    /// Runs <paramref name="f"/> against an already-open session and disposes it
    /// afterwards, including on failure. F#'s <c>use</c> doesn't bind
    /// <see cref="IAsyncDisposable"/> (only <see cref="IDisposable"/>) — this fills that
    /// gap so callers don't have to hand-write the try/finally + <c>DisposeAsync</c> dance.
    /// The dispose itself blocks synchronously on the returned <see cref="ValueTask"/>:
    /// F#'s <c>task</c> builder does not support <c>do!</c> inside a <c>finally</c>
    /// (confirmed empirically — FS0750), and session disposal is a cheap connection-close,
    /// not a await-worthy operation.
    /// </summary>
    let runSession<'Session, 'a when 'Session :> IAsyncDisposable> (session: 'Session) (f: 'Session -> Task<'a>) : Task<'a> =
        task {
            try
                return! f session
            finally
                session.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    /// <summary>
    /// Like <see cref="runSession"/>, for a body that returns a <c>Result</c>: commits the
    /// session when it returns <c>Ok</c>, discards its writes when it returns <c>Error</c>,
    /// and disposes it either way. <c>Ok</c> then really means "done" — the forgotten
    /// <c>CommitAsync</c> after a successful <c>trySaveAsync</c> (feedback F-18) cannot
    /// happen. An exception still disposes without commit, which rolls back and logs.
    /// </summary>
    let inline runSessionCommitted< ^Session, 'a, 'e
        when ^Session :> IAsyncDisposable
        and ^Session: (member CommitAsync: CancellationToken -> Task)
        and ^Session: (member DiscardAsync: CancellationToken -> Task)>
        (session: ^Session)
        (f: ^Session -> Task<Result<'a, 'e>>)
        : Task<Result<'a, 'e>> =
        let commit () = (^Session: (member CommitAsync: CancellationToken -> Task) (session, CancellationToken.None))
        let discard () = (^Session: (member DiscardAsync: CancellationToken -> Task) (session, CancellationToken.None))
        runSession session (fun s ->
            task {
                let! result = f s
                match result with
                | Ok _ -> do! commit ()
                | Error _ -> do! discard ()
                return result
            })

    /// <see cref="KernelError"/>-returning counterpart of <c>SaveAsync</c>.
    let inline trySaveAsync< ^Session, 'T
        when ^Session: (member SaveAsync: 'T * int64 * CancellationToken -> Task<SaveResult>)>
        (session: ^Session)
        (document: 'T)
        (expectedVersion: int64)
        : Task<Result<SaveResult, KernelError>> =
        task {
            try
                let! result = (^Session: (member SaveAsync: 'T * int64 * CancellationToken -> Task<SaveResult>) (session, document, expectedVersion, CancellationToken.None))
                return Ok result
            with
            | KernelErrorClassifier.Known err -> return Error err
        }

    /// <see cref="KernelError"/>-returning counterpart of <c>PatchAsync</c>.
    /// <paramref name="expectedVersion"/> follows ADR-012: <c>None</c> means deliberate
    /// field-level last-writer-wins, not "don't care about conflicts entirely".
    let inline tryPatchAsync< ^Session, 'T
        when ^Session: (member PatchAsync: string * Action<PatchBuilder<'T>> * Nullable<int64> * CancellationToken -> Task<SaveResult>)>
        (session: ^Session)
        (id: string)
        (patch: PatchBuilder<'T> -> unit)
        (expectedVersion: int64 option)
        : Task<Result<SaveResult, KernelError>> =
        task {
            try
                let! result =
                    (^Session: (member PatchAsync: string * Action<PatchBuilder<'T>> * Nullable<int64> * CancellationToken -> Task<SaveResult>)
                        (session, id, Action<_>(patch), Option.toNullable expectedVersion, CancellationToken.None))
                return Ok result
            with
            | KernelErrorClassifier.Known err -> return Error err
        }
