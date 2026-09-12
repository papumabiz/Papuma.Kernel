// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(model);

        ConnectionString = connectionString;
        Model = model;
        _notifyWaiters = notifyWaiters;
    }

    /// <summary>
    /// Opens a unit-of-work session bound to the given scope. Dispose the session;
    /// uncommitted writes roll back.
    /// </summary>
    /// <param name="scope">The scope context all session operations run under.</param>
    /// <param name="options">Optional session metadata (correlation/causation/actor).</param>
    public SqliteDocumentSession OpenSession(ScopeContext scope, SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new SqliteDocumentSession(ConnectionString, Model, scope, options, _notifyWaiters);
    }
}
