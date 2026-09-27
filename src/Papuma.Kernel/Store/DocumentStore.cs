// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Store;

/// <summary>
/// Entry point to the document store. Create once (with the startup-built
/// <see cref="KernelModel"/>), open one <see cref="DocumentSession"/> per scope
/// and unit of work.
/// </summary>
public sealed class DocumentStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger? _logger;

    /// <summary>Gets the kernel model this store operates on.</summary>
    public KernelModel Model { get; }

    /// <summary>Gets the data source for kernel-internal tooling (e.g. <c>GdprExport</c>).</summary>
    internal NpgsqlDataSource DataSource => _dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentStore"/> class.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="model">The startup-built kernel model.</param>
    public DocumentStore(NpgsqlDataSource dataSource, KernelModel model)
        : this(dataSource, model, logger: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentStore"/> class with a logger —
    /// sessions report a dispose with uncommitted writes through it.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="model">The startup-built kernel model.</param>
    /// <param name="logger">The logger sessions report through; <c>null</c> disables logging.</param>
    public DocumentStore(NpgsqlDataSource dataSource, KernelModel model, ILogger<DocumentStore>? logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(model);
        _dataSource = dataSource;
        Model = model;
        _logger = logger;
    }

    /// <summary>
    /// Opens a unit-of-work session bound to the given scope. Commit it, or dispose it to
    /// roll back — a dispose with uncommitted writes is logged (architecture §5).
    /// </summary>
    /// <param name="scope">The scope context all session operations run under.</param>
    /// <param name="options">Optional session metadata (correlation/causation/actor).</param>
    public DocumentSession OpenSession(ScopeContext scope, SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new DocumentSession(_dataSource, Model, scope, options, _logger);
    }
}
