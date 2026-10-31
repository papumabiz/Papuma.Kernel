// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
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
    private readonly IHashKeyProvider? _hashKeys;

    /// <summary>Gets the SQLite connection string sessions open against.</summary>
    internal string ConnectionString { get; }

    /// <summary>Gets the kernel model this store operates on.</summary>
    public KernelModel Model { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDocumentStore"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>).</param>
    /// <param name="model">The startup-built kernel model.</param>
    /// <param name="notifier">
    /// The in-process wakeup the feed processors wait on, signalled after a session commits writes — the
    /// single-writer-embedded replacement for Postgres's LISTEN/NOTIFY. Pass the same instance to the
    /// <see cref="SqliteChangeFeedProcessor"/> and <c>SqliteEventFeedProcessor</c> of this file, or they only see
    /// commits at their poll interval. <c>AddPapumaKernelLocal</c> wires it; <c>null</c> is fine for a store used
    /// without feed processors (tests, a document-only consumer).
    /// </param>
    public SqliteDocumentStore(string connectionString, KernelModel model, SqliteChangeNotifier? notifier = null)
        : this(connectionString, model, notifier, logger: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDocumentStore"/> class with a
    /// logger — sessions report a dispose with uncommitted writes through it.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>).</param>
    /// <param name="model">The startup-built kernel model.</param>
    /// <param name="notifier">The in-process wakeup of the feed processors, or <c>null</c> (see the other constructor).</param>
    /// <param name="logger">The logger sessions report through; <c>null</c> disables logging.</param>
    public SqliteDocumentStore(
        string connectionString, KernelModel model, SqliteChangeNotifier? notifier, ILogger<SqliteDocumentStore>? logger)
        : this(connectionString, model, notifier, logger, hashKeys: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDocumentStore"/> class with a logger and
    /// the key provider of the keyed <c>Hash</c> policy (ADR-025).
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>).</param>
    /// <param name="model">The startup-built kernel model.</param>
    /// <param name="notifier">The in-process wakeup of the feed processors, or <c>null</c> (see the first constructor).</param>
    /// <param name="logger">The logger sessions report through; <c>null</c> disables logging.</param>
    /// <param name="hashKeys">
    /// The hash key provider; <c>null</c> keeps the legacy v1 hash for <c>[TrackHash]</c> fields
    /// (a transitional state).
    /// </param>
    public SqliteDocumentStore(
        string connectionString,
        KernelModel model,
        SqliteChangeNotifier? notifier,
        ILogger<SqliteDocumentStore>? logger,
        IHashKeyProvider? hashKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(model);
        model.EnsureHashKeysConfigured(hashKeys);

        ConnectionString = connectionString;
        Model = model;
        _notifyWaiters = notifier is null ? null : notifier.TrySignal;
        _logger = logger;
        _hashKeys = hashKeys;
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
        return new SqliteDocumentSession(ConnectionString, Model, scope, options, _notifyWaiters, _logger, _hashKeys);
    }

    /// <summary>
    /// Opens a session that only reads (review L-5). A session from <see cref="OpenSession"/> takes the file's single
    /// write lock with its first operation and holds it until commit or dispose — kept open over user interaction (load,
    /// form, save) it blocks every writer, the feed workers included. A <see cref="SqliteReadSession"/> runs in a deferred
    /// transaction instead: under SQLite's WAL mode it takes no write lock and blocks no writer. It has no write methods;
    /// save in a short session from <see cref="OpenSession"/>.
    /// </summary>
    /// <param name="scope">The scope context all session operations run under.</param>
    public SqliteReadSession OpenReadSession(ScopeContext scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new SqliteReadSession(new SqliteDocumentSession(
            ConnectionString, Model, scope, options: null, _notifyWaiters, _logger, _hashKeys, readOnly: true));
    }
}
