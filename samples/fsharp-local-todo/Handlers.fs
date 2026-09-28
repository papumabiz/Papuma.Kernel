// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

module FSharpLocalTodo.Handlers

open System.Threading.Tasks
open Papuma.Kernel.Changes
open Papuma.Kernel.Processing

/// The change feed still works from an F#-authored handler — IChangeHandler is a plain
/// interface, nothing about it needs the C# facade. Registered via
/// `AddChangeHandler<TodoChangeLogger>()` in Program.fs (ADR-009: ordered per-handler
/// delivery, persisted checkpoint, retry with backoff — all unaffected by the language
/// the handler happens to be written in). An effect (it prints), so it starts at the
/// feed head on its first registration (ADR-024) — a projection would implement
/// IProjection instead.
[<StartsAtFeedHead>]
type TodoChangeLogger() =
    interface IChangeHandler with
        member _.Name = "todo_change_logger"

        member _.HandleAsync(change: ChangeRecord, _ct) =
            printfn
                "[feed] seq=%d %A %s/%s -> v%d"
                change.Seq
                change.Operation
                change.DocumentType
                change.DocumentId
                change.Version

            Task.CompletedTask
