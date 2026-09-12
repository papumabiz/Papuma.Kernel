# fsharp-local-todo — Papuma.Kernel.FSharp, end to end

A minimal F# ASP.NET Core API over `Papuma.Kernel.Local` (SQLite — no server, no
Docker), using every piece of [`Papuma.Kernel.FSharp`](../../src/Papuma.Kernel.FSharp):
quotation-based `Patch`, `Result`-returning writes, and the `IAsyncDisposable` session
runner. Design reasoning behind the facade:
[concepts.md §29](../../docs/concepts.md#29-f-as-a-facade-not-a-rewrite--and-why-the-wire-format-stays-closed).

| Concept | Where in this sample |
|---|---|
| `[<UniqueKey>]` on a record field (no quotation needed to declare it) | [`Domain.fs`](Domain.fs) — `TodoItem.Slug` |
| `SetQ`/`IncrementQ` in one `Patch` call | `POST /todos/{id}/complete` — sets `Done`, atomically bumps `TouchCount` |
| `trySaveAsync`/`tryPatchAsync` — `Result<T, KernelError>` instead of exceptions | every handler in [`Program.fs`](Program.fs); `UniqueKeyViolation`/`DocumentNotFound` map straight to HTTP 409/404 |
| `runSession` — the `IAsyncDisposable` gap F#'s `use` doesn't cover | every handler wraps its body in `runSession` |
| `CommitAsync` is not automatic | every write calls it explicitly before responding — see the comment in `createTodo`; skipping it is an easy mistake (a session's writes stay invisible to every other session until committed, silently rolled back on dispose) |
| An F#-authored `IChangeHandler` on the feed | [`Handlers.fs`](Handlers.fs) — `TodoChangeLogger`, logs every change to the console |

## Run it

No server, no Docker — SQLite is embedded:

```bash
dotnet run --project samples/fsharp-local-todo
```

The schema is created idempotently at startup in `fsharp-local-todo.db` (next to the
built binary). Delete that file to start over.

## Try it

```bash
# Create — 201, or 409 if the slug is already taken
curl -s http://localhost:5098/todos -X POST -H "Content-Type: application/json" \
  -d '{"title":"Buy milk","slug":"buy-milk"}'

# Load — 200, or 404
curl -s http://localhost:5098/todos/<id>

# Complete — Set + Increment in one Patch call, 200 or 404
curl -s http://localhost:5098/todos/<id>/complete -X POST
```

See [`requests/todos.http`](requests/todos.http) for the same, runnable from an
editor's HTTP client.

## What this sample deliberately skips

No approval workflows, sagas, realtime push, MCP endpoint or dashboard — those are
`Papuma.Kernel` (Postgres)-only surfaces, covered by
[`shop-minimal-api`](../shop-minimal-api). This sample's only job is to prove the F#
facade end to end on the SQLite kernel, which had no sample of its own before this one.
