// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Configures the SQLite kernel bootstrap (<c>AddPapumaKernelLocal</c>) — counterpart of
/// <c>PapumaKernelOptions</c>. Database, model, schema management and feed processing.
/// </summary>
public sealed class PapumaKernelLocalOptions
{
    /// <summary>
    /// Gets or sets the SQLite database file path. Mutually exclusive with
    /// <see cref="ConnectionString"/> — a plain <c>Data Source=...</c> connection string
    /// is built from it.
    /// </summary>
    public string? DbPath { get; set; }

    /// <summary>
    /// Gets or sets a raw SQLite connection string (e.g. for
    /// <c>Data Source=file.db;Cache=Shared</c> or other tuning). Mutually exclusive with
    /// <see cref="DbPath"/>.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets whether the kernel schema — and after it every registered
    /// <see cref="ISqliteSchemaContributor"/> — is applied on startup (default: true).
    /// </summary>
    public bool EnsureSchema { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the change feed and event feed run as hosted background
    /// workers (default: true). Turn it off in a host-based test fixture that drives the
    /// feeds itself with <c>ProcessOnceAsync</c> until a cycle delivers nothing: next to a
    /// hosted worker, such a cycle races it — it can report nothing while the worker is
    /// still delivering. Also for instances that should not process feeds, such as
    /// web nodes next to dedicated worker nodes. Schema setup, the processors and event
    /// retention are registered either way.
    /// </summary>
    public bool RunFeedWorkers { get; set; } = true;

    /// <summary>Gets the feed engine options (shared by change feed and event log).</summary>
    public ChangeFeedProcessorOptions Processing { get; } = new();

    /// <summary>
    /// Gets or sets the interval of the event retention worker (default: 1 hour). The
    /// worker only runs when at least one event type declares a retention.
    /// </summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    internal Action<KernelModelBuilder>? ModelConfiguration { get; private set; }

    /// <summary>
    /// Configures the kernel model (documents, events, policies, keys, upcasters).
    /// Multiple calls compose.
    /// </summary>
    public void Model(Action<KernelModelBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ModelConfiguration = ModelConfiguration is null ? configure : ModelConfiguration + configure;
    }

    /// <summary>Resolves the effective connection string from whichever of the two was configured.</summary>
    internal string ResolveConnectionString() =>
        ConnectionString ?? $"Data Source={DbPath}";

    internal void Validate()
    {
        if ((DbPath is null) == (ConnectionString is null))
        {
            throw new InvalidOperationException(
                "Configure exactly one of PapumaKernelLocalOptions.DbPath or .ConnectionString.");
        }

        if (ModelConfiguration is null)
        {
            throw new InvalidOperationException(
                "Configure the kernel model via PapumaKernelLocalOptions.Model(m => m.Document<...>()).");
        }

        if (RetentionInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("RetentionInterval must be positive.");
        }

        Processing.Validate();
    }
}
