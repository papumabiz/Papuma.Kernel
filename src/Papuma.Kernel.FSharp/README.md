# Papuma.Kernel.FSharp

F#-idiomatic facade over [Papuma Kernel](https://github.com/papumabiz/Papuma.Kernel)'s
write path. Not a separate kernel and not a rewrite — it references
`Papuma.Kernel.Core` only, calls the same `SaveAsync`/`PatchAsync` either kernel already
exposes, and works alongside **either** `Papuma.Kernel` (PostgreSQL) or
`Papuma.Kernel.Local` (SQLite). C# consumers of those packages see no change at all.

Design reasoning (why C# stays the core language, where F# genuinely needs help vs.
where it doesn't, why the wire format stays closed): concepts.md §29, shipped in the
kernel packages' own `docs/` folder, or
[read it on GitHub](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/concepts.md#29-f-as-a-facade-not-a-rewrite--and-why-the-wire-format-stays-closed).

## Install

Alongside whichever kernel you use:

```bash
dotnet add package Papuma.Kernel.Local    # or Papuma.Kernel
dotnet add package Papuma.Kernel.FSharp
```

## Patch with F# lambdas

The kernel's `PatchBuilder<T>` works from F# as it is: F# converts a lambda to a LINQ
expression at the method call. `Remove` and `Increment` take an `obj`-typed lambda, so
`box` the field — the kernel unwraps F#'s `box` like C#'s implicit conversion:

```fsharp
session.PatchAsync<Order>(id, fun p ->
    p.Set((fun x -> x.Status), "shipped")
     .Remove(fun x -> box x.DraftNote)
     .Increment((fun x -> box x.RevisionCount), 1L)
    |> ignore)
```

A simple property-access chain (`x.Field`, `x.Nested.Field`), same as from C#. Where the
document type is not fixed by a type argument — `tryPatchAsync`, or several record types
sharing a field name — annotate the parameter, `(fun (x: Order) -> x.Status)`: F# infers
a record type from a field name by picking the most recently declared one.

> **Deprecated: `SetQ` / `RemoveQ` / `IncrementQ`.** The quotation-based members
> (`p.SetQ(<@ fun x -> x.Status @>, "shipped")`) were built on the premise that F#
> cannot produce LINQ expressions from lambdas. It can; the actual obstacle was the
> kernel not recognizing F#'s `box`, now fixed. They still work, are marked
> `[<Obsolete>]`, and are planned for removal in 2.0 — switch to the lambda form above.

## Keys from F# — `box` lambdas and tuples

Key declarations and lookups take a lambda to `obj`; F# converts it at the method
call. Box the property, and declare a composite key (ADR-020) with a **tuple**:

```fsharp
m.Document<Ticket>(fun d ->
    d.UniqueKey(fun x -> box x.Email)                      // single field
     .UniqueKey(fun x -> box (x.ProjectId, x.Number))      // composite: tuple, in key order
    |> ignore)

// F# picks the single-value overload for a bare array — annotate the values:
let values: IReadOnlyList<obj> = [| "p1" :> obj; 42 :> obj |]
let! ticket = session.LoadByKeyAsync<Ticket>((fun x -> box (x.ProjectId, x.Number)), values)
```

Anonymous records (`{| ProjectId = …; Number = … |}`) are rejected: F# sorts their
fields alphabetically and lowers the construction to a block, so the declared order
would not survive. `[<UniqueKey>]` on a record field keeps working for single-field keys.

## `Result` instead of exceptions — `trySaveAsync` / `tryPatchAsync`

`Papuma.Kernel`/`Papuma.Kernel.Local` throw `ConcurrencyException`,
`DocumentNotFoundException` and `UniqueKeyViolationException` for the three *expected*
write-time outcomes ADR-003/ADR-006 document. F# convention prefers branching on a
`Result` over catching for outcomes like these. These wrappers catch exactly those
three, translate them to `KernelError`, and rethrow anything else (schema-upcast
requirements, infra failures) unchanged:

```fsharp
open Papuma.Kernel.FSharp

let! outcome = trySaveAsync session { Id = id; Owner = "Harry"; Balance = 100 } 0L

match outcome with
| Ok result -> printfn "saved at version %d" result.Version
| Error (VersionConflict(_, _, expected, actual)) -> printfn "expected v%d, stored is v%d" expected actual
| Error (DocumentNotFound(documentType, documentId)) -> printfn "%s/%s does not exist" documentType documentId
| Error (UniqueKeyViolation(documentType, keyPath)) -> printfn "%s.%s already taken" documentType keyPath
```

`tryPatchAsync` is the same shape, taking a plain `PatchBuilder<'T> -> unit` function
(no `Action<_>` wrapper needed) and an `int64 option` for `expectedVersion` (`None` is
ADR-012's deliberate field-level last-writer-wins, not "skip the concurrency check
because I don't care"):

```fsharp
let! outcome =
    tryPatchAsync session id
        (fun p -> p.Set((fun (x: Order) -> x.Status), "shipped") |> ignore)
        None
```

Both work against `DocumentSession` (Postgres) and `SqliteDocumentSession` (SQLite)
through one implementation — the two are unrelated `sealed` classes with identical
method shapes, so this is written against statically resolved type parameters (SRTP)
rather than a shared interface.

## `runSession` — the `IAsyncDisposable` gap

F#'s `use` binds `IDisposable`, not `IAsyncDisposable` — sessions implement the latter.
`runSession` runs a function against an open session and disposes it afterwards,
including on failure, without a hand-written `try`/`finally`.

A session's writes are rolled back on dispose unless `CommitAsync` was called — and an
`Ok` from `trySaveAsync` reads like "done" although nothing is committed yet. For a body
that returns a `Result`, use **`runSessionCommitted`**: it commits on `Ok`, discards the
writes on `Error` (`DiscardAsync`), and disposes either way:

```fsharp
let! result =
    runSessionCommitted (store.OpenSession(scope)) (fun session ->
        trySaveAsync session order 0L)
```

Plain `runSession` leaves committing to you; a dispose with uncommitted writes rolls
back and logs a warning (event id 1001).

## What this package deliberately does not do

Discriminated-union or `'T option` fields on document types aren't supported —
the kernel's `System.Text.Json` configuration is internal and fixed on purpose (the
same JSON shape for every consumer, in every language, is what makes the change feed a
trustworthy cross-process contract). Model your storage shape as a plain record and map
at the boundary; `Option.toObj`/`Option.ofObj`/`Option.toNullable`/`Option.ofNullable`
(all in `FSharp.Core` already) usually make that a one-liner per field. See concepts.md
§29 for the full reasoning.

## Sample

[`samples/fsharp-local-todo`](https://github.com/papumabiz/Papuma.Kernel/tree/master/samples/fsharp-local-todo)
is a minimal F# ASP.NET Core API over `Papuma.Kernel.Local`, using every piece above.

## Status

Young — built and tested against `Papuma.Kernel.Local` (SQLite); the Postgres kernel
shares the exact method shapes this package is written against but hasn't been
exercised against it directly yet. No breaking changes planned, but treat it as
earlier-stage than either kernel package until it's seen real use.
