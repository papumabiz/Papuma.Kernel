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

## Quotation-based Patch — `SetQ` / `RemoveQ` / `IncrementQ`

C#'s `x => x.Field` lambdas convert to `Expression<Func<T,TValue>>` by compiler magic
F# doesn't have. These extension members on `PatchBuilder<T>` take a plain quotation
instead, converted by a small hand-rolled walker (not
`LeafExpressionConverter` — that helper only bridges quotations that already construct
a `System.Func`, which is more ceremony than this needs):

```fsharp
open Papuma.Kernel.FSharp

session.PatchAsync<Order>(id, fun p ->
    p.SetQ(<@ fun x -> x.Status @>, "shipped")
     .RemoveQ(<@ fun x -> x.DraftNote @>)
     .IncrementQ(<@ fun x -> x.RevisionCount @>, 1L)
    |> ignore)
```

Same scope restriction as the C# API it mirrors: a simple property-access chain
(`x.Field`, `x.Nested.Field`) — anything richer fails the same way the kernel's own
`JsonPathResolver.Resolve` already rejects it on the C# side.

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
        (fun p -> p.SetQ(<@ fun x -> x.Status @>, "shipped") |> ignore)
        None
```

Both work against `DocumentSession` (Postgres) and `SqliteDocumentSession` (SQLite)
through one implementation — the two are unrelated `sealed` classes with identical
method shapes, so this is written against statically resolved type parameters (SRTP)
rather than a shared interface.

## `runSession` — the `IAsyncDisposable` gap

F#'s `use` binds `IDisposable`, not `IAsyncDisposable` — sessions implement the latter.
`runSession` runs a function against an open session and disposes it afterwards,
including on failure, without a hand-written `try`/`finally`:

```fsharp
let! result =
    runSession (store.OpenSession(scope)) (fun session ->
        task {
            let! outcome = trySaveAsync session order 0L
            return outcome
        })
```

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
