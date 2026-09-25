// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

// F# sample for Papuma.Kernel.FSharp, over Papuma.Kernel.Local (SQLite — no server, no
// Docker). Walkthrough in README.md; the facade itself is documented in
// Papuma.Kernel.FSharp's own README and docs/concepts.md §29.
// Handlers read HttpContext directly and resolve the store from RequestServices —
// deliberately not the typed minimal-API parameter binding C# samples use, to keep this
// sample independent of any F#-specific minimal-API binding behavior.

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Papuma.Kernel.FSharp
open Papuma.Kernel.Hosting
open Papuma.Kernel.Store
open Papuma.Kernel.Tenancy
open FSharpLocalTodo.Domain
open FSharpLocalTodo.Handlers

// Single-writer local app — one fixed tenant, same shape as the kernel's own
// getting-started example (`ScopeContext.Tenant("local")`).
let private scope = ScopeContext.Tenant("local")

let private store (ctx: HttpContext) = ctx.RequestServices.GetRequiredService<SqliteDocumentStore>()

let private createTodo (ctx: HttpContext) : Task =
    runSession
        (store(ctx).OpenSession(scope))
        (fun session ->
            task {
                let! body = ctx.Request.ReadFromJsonAsync<CreateTodoRequest>()

                match body with
                | null ->
                    ctx.Response.StatusCode <- 400
                    do! ctx.Response.WriteAsJsonAsync {| error = "expected a JSON body with title and slug" |}
                | body ->
                    let todo =
                        { Id = Guid.NewGuid().ToString("N")
                          Title = body.Title
                          Slug = body.Slug
                          Done = false
                          TouchCount = 0 }

                    let! outcome = trySaveAsync session todo 0L

                    match outcome with
                    | Ok result ->
                        // Nothing a session writes is visible to other sessions until
                        // CommitAsync — disposing without it is an implicit rollback
                        // (SqliteDocumentSession.cs remarks). Easy to miss for a single
                        // write since it "looks" done already; the kernel doesn't
                        // auto-commit a lone Save/Patch on your behalf.
                        do! session.CommitAsync()
                        ctx.Response.StatusCode <- 201
                        do! ctx.Response.WriteAsJsonAsync(toResponse result.Version todo)
                    | Error(UniqueKeyViolation(_, keyPath)) ->
                        ctx.Response.StatusCode <- 409
                        do! ctx.Response.WriteAsJsonAsync {| error = $"slug already taken ({keyPath})" |}
                    | Error other ->
                        ctx.Response.StatusCode <- 409
                        do! ctx.Response.WriteAsJsonAsync {| error = string other |}
            })
    :> Task

let private getTodo (ctx: HttpContext) : Task =
    runSession
        (store(ctx).OpenSession(scope))
        (fun session ->
            task {
                let id = string ctx.Request.RouteValues["id"]
                let! loaded = session.LoadAsync<TodoItem>(id)

                match loaded with
                | null -> ctx.Response.StatusCode <- 404
                | loaded -> do! ctx.Response.WriteAsJsonAsync(toResponse loaded.Version loaded.Document)
            })
    :> Task

let private completeTodo (ctx: HttpContext) : Task =
    runSession
        (store(ctx).OpenSession(scope))
        (fun session ->
            task {
                let id = string ctx.Request.RouteValues["id"]

                // Two Patch operations in one call: Set the boolean, Increment the
                // touch counter atomically — no read-modify-write race on TouchCount
                // (ADR-012).
                let! outcome =
                    tryPatchAsync
                        session
                        id
                        (fun p ->
                            // Annotate x: TodoResponse also has Done/TouchCount, and F# infers
                            // the most recently declared record type from a field name.
                            p.Set((fun (x: TodoItem) -> x.Done), true)
                             .Increment((fun (x: TodoItem) -> box x.TouchCount), 1L)
                            |> ignore)
                        None

                match outcome with
                | Ok result ->
                    do! session.CommitAsync() // see the comment in createTodo
                    let! loaded = session.LoadAsync<TodoItem>(id)

                    match loaded with
                    | null -> ctx.Response.StatusCode <- 404
                    | loaded -> do! ctx.Response.WriteAsJsonAsync(toResponse result.Version loaded.Document)
                | Error(DocumentNotFound(_, _)) -> ctx.Response.StatusCode <- 404
                | Error other ->
                    ctx.Response.StatusCode <- 409
                    do! ctx.Response.WriteAsJsonAsync {| error = string other |}
            })
    :> Task

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    builder.Services
        .AddPapumaKernelLocal(fun o ->
            o.DbPath <- Path.Combine(AppContext.BaseDirectory, "fsharp-local-todo.db")
            o.Model(fun m -> m.Document<TodoItem>() |> ignore))
        .AddChangeHandler<TodoChangeLogger>()
    |> ignore

    let app = builder.Build()

    app.MapPost("/todos", Func<HttpContext, Task>(createTodo)) |> ignore
    app.MapGet("/todos/{id}", Func<HttpContext, Task>(getTodo)) |> ignore
    app.MapPost("/todos/{id}/complete", Func<HttpContext, Task>(completeTodo)) |> ignore

    app.Run()
    0
