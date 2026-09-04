// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

/// Shared test bootstrap — one temp-file SQLite db per call, torn down afterwards. The
/// self-contained equivalent of Papuma.Kernel.Local.Tests' SqliteFixture, built purely off
/// the public `AddPapumaKernelLocal` bootstrap since this project has no InternalsVisibleTo
/// access to that internal fixture (same as a real consumer would use).
module Papuma.Kernel.FSharp.Tests.Infrastructure

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Papuma.Kernel.Hosting
open Papuma.Kernel.Store
open Papuma.Kernel.Tenancy

let newId () = Guid.NewGuid().ToString("N")

let newTenant () = ScopeContext.Tenant($"t{Guid.NewGuid():N}")

/// Builds a store for the given document type <c>'T</c> (attribute-declared keys, e.g.
/// <c>[&lt;UniqueKey&gt;]</c>, apply automatically — no explicit model configuration
/// needed for the common case) and runs <paramref name="run"/> against it.
let withStore<'T when 'T: not struct and 'T: not null> (run: SqliteDocumentStore -> Task) : Task =
    task {
        let dbPath = Path.Combine(Path.GetTempPath(), $"papuma_fsharp_test_{Guid.NewGuid():N}.db")
        let builder = Host.CreateApplicationBuilder()
        builder.Logging.ClearProviders() |> ignore

        builder.Services.AddPapumaKernelLocal(fun o ->
            o.DbPath <- dbPath
            o.Model(fun m -> m.Document<'T>() |> ignore))
        |> ignore

        use host = builder.Build()
        do! host.StartAsync()

        try
            let store = host.Services.GetRequiredService<SqliteDocumentStore>()
            do! run store
        finally
            host.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
            // Microsoft.Data.Sqlite pools connections by connection string — Dispose()
            // alone doesn't release the file handle, so an immediate File.Delete throws
            // on Windows. ClearPool forces it (same fix as SqliteFixture.DisposeAsync in
            // Papuma.Kernel.Local.Tests).
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            if File.Exists dbPath then
                File.Delete dbPath
    }
