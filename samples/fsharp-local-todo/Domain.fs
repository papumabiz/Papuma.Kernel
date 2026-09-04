// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

module FSharpLocalTodo.Domain

open Papuma.Kernel.Model

/// The one document type this sample stores. `Slug` is declared unique via attribute —
/// the F#-idiomatic path (no quotation needed): see concepts.md §29.
type TodoItem =
    { Id: string
      Title: string
      [<UniqueKey>]
      Slug: string
      Done: bool
      /// Bumped by IncrementQ every time the item is touched — demonstrates atomic
      /// Increment (ADR-012), not just Set.
      TouchCount: int }

type CreateTodoRequest = { Title: string; Slug: string }

type TodoResponse =
    { Id: string
      Title: string
      Slug: string
      Done: bool
      TouchCount: int
      Version: int64 }

let toResponse (version: int64) (todo: TodoItem) : TodoResponse =
    { Id = todo.Id
      Title = todo.Title
      Slug = todo.Slug
      Done = todo.Done
      TouchCount = todo.TouchCount
      Version = version }
