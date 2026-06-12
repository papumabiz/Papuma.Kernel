// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

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
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(model);
        _dataSource = dataSource;
        Model = model;
    }

    /// <summary>
    /// Opens a unit-of-work session bound to the given scope. Dispose the session;
    /// uncommitted writes roll back (architecture §5).
    /// </summary>
    /// <param name="scope">The scope context all session operations run under.</param>
    /// <param name="options">Optional session metadata (correlation/causation/actor).</param>
    public DocumentSession OpenSession(ScopeContext scope, SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new DocumentSession(_dataSource, Model, scope, options);
    }
}
