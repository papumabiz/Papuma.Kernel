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

    /// Quotation-based <see cref="KernelError"/> counterpart of <c>SaveAsync</c>.
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

    /// Quotation-based <see cref="KernelError"/> counterpart of <c>PatchAsync</c>.
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
