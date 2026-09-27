// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Store;

/// <summary>
/// Entry point to the SQLite document store — the <c>DocumentStore</c> (Postgres)
/// counterpart for a single-writer embedded kernel. Create once (with the startup-built
/// <see cref="KernelModel"/>), open one <see cref="SqliteDocumentSession"/> per unit of work.
/// </summary>
public sealed class SqliteDocumentStore
{
    private readonly Action? _notifyWaiters;
    private readonly ILogger? _logger;

    /// <summary>Gets the SQLite connection string sessions open against.</summary>
    internal string ConnectionString { get; }

    /// <summary>Gets the kernel model this store operates on.</summary>
    public KernelModel Model { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDocumentStore"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>).</param>
    /// <param name="model">The startup-built kernel model.</param>
    /// <param name="notifyWaiters">
    /// Optional in-process wakeup hook, invoked after a session commits writes — the
    /// single-writer-embedded replacement for Postgres's LISTEN/NOTIFY (ADR-010 does not
    /// apply here: there is no concurrent-writer visibility gap to guard against). Wired
    /// to <c>Papuma.Kernel.Local.Processing.SqliteChangeNotifier.TrySignal</c> by
    /// <c>AddPapumaKernelLocal</c>; <c>null</c> is a valid, fully functional choice for a
    /// store used without a feed processor (e.g. tests, or a document-only consumer).
    /// </param>
    public SqliteDocumentStore(string connectionString, KernelModel model, Action? notifyWaiters = null)
        : this(connectionString, model, notifyWaiters, logger: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDocumentStore"/> class with a
    /// logger — sessions report a dispose with uncommitted writes through it.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>).</param>
    /// <param name="model">The startup-built kernel model.</param>
    /// <param name="notifyWaiters">Optional in-process wakeup hook (see the other constructor).</param>
    /// <param name="logger">The logger sessions report through; <c>null</c> disables logging.</param>
    public SqliteDocumentStore(
        string connectionString, KernelModel model, Action? notifyWaiters, ILogger<SqliteDocumentStore>? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(model);

        ConnectionString = connectionString;
        Model = model;
        _notifyWaiters = notifyWaiters;
        _logger = logger;
    }

    /// <summary>
    /// Opens a unit-of-work session bound to the given scope. Commit it, or dispose it to
    /// roll back — a dispose with uncommitted writes is logged.
    /// </summary>
    /// <param name="scope">The scope context all session operations run under.</param>
    /// <param name="options">Optional session metadata (correlation/causation/actor).</param>
    public SqliteDocumentSession OpenSession(ScopeContext scope, SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new SqliteDocumentSession(ConnectionString, Model, scope, options, _notifyWaiters, _logger);
    }
}
